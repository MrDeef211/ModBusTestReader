namespace ModBusV2
{
    public class ConcurentArrayListOfArray<TKey, TArray> : IDisposable
        where TKey : notnull
        where TArray : struct
    {
        private Dictionary<TKey, TArray[]> _arrays = new();

        public Dictionary<TKey, TArray[]> Arrays => new(_arrays);

        public IReadOnlyCollection<TKey> Keys => _arrays.Keys;

        private readonly ReaderWriterLockSlim[] _locks = Enumerable.Range(0, 16)
            .Select(_ => new ReaderWriterLockSlim()).ToArray();

        private ReaderWriterLockSlim GetLock(TKey key)
        {
            int hash = key.GetHashCode();
            int index = Math.Abs(hash) % _locks.Length;
            return _locks[index];
        }

        public int GetLength(TKey key)
        {
            var rwLock = GetLock(key);

            rwLock.EnterReadLock();
            try
            {
                return _arrays[key].Length;
            }
            catch
            {
                return 0;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public bool TryGetArray(TKey key, out TArray[] array)
        {
            var rwLock = GetLock(key);

            rwLock.EnterReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    array = [];
                    return false;
                }

                array = _arrays[key];
                return true;
            }
            catch
            {
                array = [];
                return false;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public bool TrySetArray(TKey key, in TArray[] array)
        {
            var rwLock = GetLock(key);

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    return false;
                }

                rwLock.EnterWriteLock();
                try
                {
                    _arrays[key] = array;
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }


        public bool TrySetArray(TKey key, ReadOnlySpan<TArray> array)
        {
            var rwLock = GetLock(key);

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    return false;
                }

                rwLock.EnterWriteLock();
                try
                {
                    if (array.TryCopyTo(_arrays[key].AsSpan()))
                        return true;
                    return false;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }

        public bool TryGetValue(TKey key, int index, out TArray value)
        {
            var rwLock = GetLock(key);

            rwLock.EnterReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    value = default;
                    return false;
                }

                value = _arrays[key][index];
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }

        public bool TrySetValue(TKey key, int index, TArray value)
        {
            var rwLock = GetLock(key);

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    return false;
                }

                rwLock.EnterWriteLock();
                try
                {
                    _arrays[key][index] = value;
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }

        public bool TryAdd(TKey key, in TArray[] array)
        {
            var rwLock = GetLock(key);

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (_arrays.ContainsKey(key))
                {
                    return false;
                }

                rwLock.EnterWriteLock();
                try
                {
                    _arrays.Add(key, array);
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }

        public bool TryRemove(TKey key, out TArray[] array)
        {
            var rwLock = GetLock(key);

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    array = [];
                    return false;
                }

                rwLock.EnterWriteLock();
                try
                {
                    array = _arrays[key];
                    _arrays.Remove(key);
                    return true;
                }
                catch
                {
                    array = [];
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                array = [];
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }

        public bool TrySetLength(TKey key, int length)
        {
            var rwLock = GetLock(key);

            var newArray = new TArray[length];

            rwLock.EnterUpgradeableReadLock();
            try
            {
                if (!_arrays.ContainsKey(key))
                {
                    return false;
                }

                Span<TArray> span = _arrays[key].AsSpan();
                bool extend = length > span.Length;

                if (extend)
                {
                    span.CopyTo(newArray);
                }
                else
                {
                    span.Slice(0, length).CopyTo(newArray);
                }

                rwLock.EnterWriteLock();
                try
                {
                    _arrays[key] = newArray;
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    rwLock.ExitWriteLock();
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                rwLock.ExitUpgradeableReadLock();
            }
        }

        public static explicit operator Dictionary<TKey, TArray[]>(
            ConcurentArrayListOfArray<TKey, TArray> concurentArrayListOfArray)
        {
            return concurentArrayListOfArray.Arrays;
        }

        public void Dispose()
        {
            for (int i = 0; i < _locks.Length; i++)
            {
                var l = Interlocked.Exchange(ref _locks[i]!, null);
                l?.Dispose();
            }
        }
    }
}
