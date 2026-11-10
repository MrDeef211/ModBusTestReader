using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ModBusV2
{
    public class ModBusCollector : IDisposable
    {
        // Глобальный токет отмены всех операций
        private CancellationTokenSource _cancellationTokenSource = new();

        // Событие получения ответа на команду
        private ConcurrentDictionary<ushort, TaskCompletionSource> _commandResponseReceived = new();

        // Событие получения ответа на запрос
        private ConcurrentDictionary<ushort, TaskCompletionSource<ushort>> _requestResponseReceived = new();

        // Список активных клиентов + их стримы 
        private ConcurrentDictionary<TcpClient, NetworkStream> _clients = new();

        // Кешированный список аддресс - стрим для удобства отправки
        /// <summary>
        /// ПОТОКОНЕБЕЗОПАСНО
        /// </summary>
        public Dictionary<IPAddress, NetworkStream> _streams = new();

        // Кешированные локально данные регистров
        private ConcurentArrayListOfArray<IPAddress, ushort> _deviceRegisters = new();

        // Актуальные для принятия транзакции
        private ConcurrentDictionary<ushort, bool> _activeTransactions = new();

        private List<Task> _tasks = new List<Task>();

        // Данные для команды отправки команды записи
        private WriteCommand _writeCommand = new();

        // Данные для запроса данных первой ячейки
        private ReadRequest _readRequest = new();

        // ID текущей транзакции
        private ushort _transactionID = 0;

        // Id устройств в сети
        const byte _deviceId = 1;

        // Флаг, работает ли сейчас опрос
        private bool _work = false;

        public IReadOnlyDictionary<IPAddress, ushort[]> DeviceRegisters
            => (Dictionary<IPAddress, ushort[]>)_deviceRegisters;

        public IReadOnlyCollection<IPAddress> Devices => _deviceRegisters.Keys;

        public ModBusCollector()
        {

        }

        #region Переменные для отправки команд записи

        struct WriteCommand
        {
            private const byte _writeCommandLenght = 6;
            private const byte _writeCommandId = 6;

            private byte[] _writeCommandReqestData = new byte[_writeCommandLenght + 6];

            public WriteCommand()
            {
                InitializeWriteCommand();
            }

            private void InitializeWriteCommand()
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    _writeCommandReqestData.AsSpan(4, 2),
                    _writeCommandLenght);

                _writeCommandReqestData[6] = _deviceId;
                _writeCommandReqestData[7] = _writeCommandId;

            }

            public byte[] Get(ushort transactionID, ushort register, ushort value)
            {
                Span<byte> buffer = _writeCommandReqestData.AsSpan();
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(0, 2), transactionID);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(8, 2), register);
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(10, 2), value);
                return _writeCommandReqestData;
            }
        }

        #endregion

        #region Переменные для отправки запроса количества регистров

        byte[] _readRequestbuffer = new byte[512];

        int _readRequestBytesToRead = 0;

        ushort _localTransactionId;

        SemaphoreSlim _readRequestSemaphore = new SemaphoreSlim(1);

        struct ReadRequest
        {
            const byte _readRequestLenght = 6;
            const byte _readRequestId = 3;

            byte[] _readReqestData = new byte[_readRequestLenght + 6];

            public ReadRequest()
            {
                InitializeReadRequest();
            }

            public byte[] Get(ushort transactionID)
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    _readReqestData.AsSpan(0, 2),
                    transactionID);
                return _readReqestData;
            }

            private void InitializeReadRequest()
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    _readReqestData.AsSpan(4, 2),
                    _readRequestLenght);
                _readReqestData[6] = _deviceId;
                _readReqestData[7] = _readRequestId;
                BinaryPrimitives.WriteUInt16BigEndian(_readReqestData.AsSpan(8, 2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(_readReqestData.AsSpan(10, 2), 1);
            }
        }

        #endregion

        #region Старт, стоп

        public void Start()
        {
            if (_work)
            {
                return;
            }

            _tasks.Add(Task.Run(() => SendReadRequestLoop(_cancellationTokenSource.Token)));
            foreach (var device in _clients)
            {
                _tasks.Add(Task.Run(()
                    => ReadResponceLoop(device.Value, _cancellationTokenSource.Token)));
            }
            _work = true;
        }

        public void Stop()
        {
            if (!_work)
            {
                return;
            }

            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _work = false;

            try
            {
                Task.WaitAll(_tasks);
            }
            catch (Exception)
            {

            }
        }

        #endregion

        #region Ручные команды и запросы

        /// <summary>
        /// ПОТОКОНЕБЕЗОПАСНО отправляет команду об записи
        /// </summary>
        public async Task<bool> TrySendWriteCommand(
            IPAddress iP,
            ushort register,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            ushort transactionID = ++_transactionID;

            TryUpdateStreamsDictionary();

            if (!_streams.TryGetValue(iP, out var networkStream))
            {
                return false;
            }

            if (!_activeTransactions.TryAdd(transactionID, true))
                return false;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_commandResponseReceived.TryAdd(transactionID, tcs))
                return false;

            try
            {
                await networkStream
                    .WriteAsync(_writeCommand.Get(transactionID, register, value), cancellationToken)
                    .ConfigureAwait(false);

                await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                _deviceRegisters.TrySetValue(iP, register, value);

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _commandResponseReceived.TryRemove(transactionID, out _);
            }
        }

        /// <summary>
        /// Получить длину регистра (полный цикл)
        /// </summary>
        private async Task<ushort> TryGetLengthRequest(
            NetworkStream stream,
            IPAddress address,
            CancellationToken cancellationToken = default)
        {
            ushort transactionID = ++_transactionID;

            try
            {
                await _readRequestSemaphore.WaitAsync(cancellationToken);

                try
                {
                    await stream
                        .WriteAsync(_readRequest.Get(transactionID), cancellationToken)
                        .ConfigureAwait(false);

                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        _readRequestBytesToRead = await stream
                            .ReadAsync(_readRequestbuffer, cancellationToken)
                            .ConfigureAwait(false);

                        _localTransactionId = BinaryPrimitives.ReadUInt16BigEndian(
                            _readRequestbuffer
                            .AsSpan(0, 2)
                        );

                    } while (transactionID != _localTransactionId);

                    if (_readRequestbuffer[8] == 0x02)
                        return BinaryPrimitives.ReadUInt16BigEndian(
                            _readRequestbuffer.AsSpan(9, 2));
                    else
                        return 0;
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
                finally
                {
                    _readRequestSemaphore?.Release();
                }

            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            finally
            {
                _requestResponseReceived.TryRemove(transactionID, out _);
            }
        }

        /// <summary>
        /// Получить длину регистра (только запрос)
        /// </summary>
        private async Task<bool> TrySetActualLengthRequest(
            NetworkStream stream,
            IPAddress address,
            CancellationToken cancellationToken = default)
        {
            ushort transactionID = ++_transactionID;

            try
            {
                if (!_activeTransactions.TryAdd(transactionID, true))
                    return false;

                var tcs = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_requestResponseReceived.TryAdd(transactionID, tcs))
                    return false;

                await _readRequestSemaphore.WaitAsync(cancellationToken);
                try
                {
                    await stream
                        .WriteAsync(_readRequest.Get(transactionID), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                finally
                {
                    _readRequestSemaphore?.Release();
                }

                ushort length = await tcs.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                return _deviceRegisters.TrySetLength(address, length);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                _requestResponseReceived.TryRemove(transactionID, out _);
            }
        }

        #endregion

        #region Работа в цикле

        private async Task SendReadRequestLoop(CancellationToken cancellationToken = default)
        {
            ushort lenght = 6;
            ushort startIndex = 0;
            ushort indexCount = 10;
            ushort transactionID;
            byte command = 3;
            byte deviceId = 1;

            IPAddress? ip;
            byte[] reqestData = new byte[lenght + 6];
            Span<byte> reqestDataSpan = reqestData.AsSpan();

            Task cooldown;

            BinaryPrimitives.WriteUInt16BigEndian(reqestDataSpan.Slice(4, 2), lenght);
            reqestDataSpan[6] = deviceId;
            reqestDataSpan[7] = command;
            BinaryPrimitives.WriteUInt16BigEndian(reqestDataSpan.Slice(8, 2), startIndex);

            while (!cancellationToken.IsCancellationRequested)
            {
                //Опрос не чаще чем раз в 3 секунды
                cooldown = Task.Delay(3000);

                foreach (var client in _clients)
                {
                    // span не может пройти за await
                    reqestDataSpan = reqestData.AsSpan();

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    transactionID = ++_transactionID;
                    BinaryPrimitives.WriteUInt16BigEndian(reqestDataSpan.Slice(0, 2), transactionID);

                    ip = GetIp(client.Key);
                    if (ip == null)
                        return;

                    indexCount = (ushort)_deviceRegisters.GetLength(GetIp(client.Key)!);
                    BinaryPrimitives.WriteUInt16BigEndian(reqestDataSpan.Slice(10, 2), indexCount);

                    await client.Value.WriteAsync(
                        reqestData,
                        cancellationToken)
                        .ConfigureAwait(false);

                    _activeTransactions.TryAdd(transactionID, true);
                }

                await cooldown.ConfigureAwait(false);
            }
        }

        private async Task ReadResponceLoop(
            NetworkStream stream,
            CancellationToken cancellationToken = default)
        {
            EndPoint? endpoint = stream.Socket.RemoteEndPoint;
            IPAddress address;

            if (endpoint is IPEndPoint ip)
                address = ip.Address;
            else throw new NoIPExeption();

            byte[] buffer = new byte[512];
            // Сделал 256 с расчётом на то, что больше не будет
            ushort[] registersArray = new ushort[256];
            using MemoryStream message = new();

            Span<ushort> registers;
            ReadOnlySpan<ushort> registersBigEndian;
            ReadOnlySpan<byte> MBAP;
            ReadOnlySpan<byte> PDU;

            int bufferBytesRead = 0;
            ushort PDULength = 0;
            ushort pointer = 0;
            ushort bytesReaded = 0;
            ushort localTransactionID = 0;
            ushort localRegisterLength = 0;

            bool fragmented = false;

            while (!cancellationToken.IsCancellationRequested)
            {

                bufferBytesRead = await stream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);

                // старые span не работает после await
                registers = new();
                registersBigEndian = new();
                MBAP = new();
                PDU = new();

                if (bufferBytesRead == 0)
                {
                    Clear();
                    return;
                }

                pointer = 0;

                while (pointer < bufferBytesRead
                    && bufferBytesRead > 0
                    && !cancellationToken.IsCancellationRequested)
                {

                    // Если MBAP ещё не прочитан
                    if (bytesReaded < 7)
                    {
                        //Пытаюсь прочитать MBAP
                        #region Кошмар и ужас
                        if (fragmented && pointer + (7 - bytesReaded) <= bufferBytesRead)
                        {
                            message.Write(buffer.AsSpan(pointer, (7 - bytesReaded)));
                            MBAP = message.GetBuffer().AsSpan(0, 7);
                            pointer += (ushort)(7 - bytesReaded);
                            bytesReaded = 7;
                        }
                        else if (!fragmented && pointer + 7 <= bufferBytesRead)
                        {
                            MBAP = buffer.AsSpan(pointer, 7);
                            pointer += 7;
                        }
                        else
                        {
                            fragmented = true;
                            message.Write(buffer.AsSpan(pointer, bufferBytesRead - pointer));
                            bytesReaded += (ushort)(bufferBytesRead - pointer);
                            break;
                        }
                        #endregion
                    }

                    if (!fragmented)
                    {
                        localTransactionID = BinaryPrimitives.ReadUInt16BigEndian(
                            MBAP
                            .Slice(0, 2));
                        //Считаю длину PDU (Length хранит длину Unit ID + PDU)
                        PDULength = BinaryPrimitives.ReadUInt16BigEndian(MBAP.Slice(4, 2));
                    }
                    else
                    {
                        localTransactionID = BinaryPrimitives.ReadUInt16BigEndian(
                            message
                            .GetBuffer()
                            .AsSpan(0, 2));
                        PDULength = BinaryPrimitives.ReadUInt16BigEndian(
                            message
                            .GetBuffer()
                            .AsSpan(4, 2));
                    }

                    if (PDULength == 0)
                    {
                        Clear();
                        continue;
                    }

                    // Length хранит длину Unit ID + PDU
                    PDULength--;

                    //Пытаюсь прочитать PDU
                    #region Кошмар и ужас
                    if (fragmented && pointer + (PDULength + 7 - bytesReaded) <= bufferBytesRead)
                    {
                        message.Write(buffer.AsSpan(pointer, (PDULength + 7 - bytesReaded)));
                        PDU = message.GetBuffer().AsSpan(7, PDULength);
                        pointer += (ushort)(PDULength + 7 - bytesReaded);
                        bytesReaded = (ushort)(PDULength + 7);
                    }
                    else if (!fragmented && pointer + PDULength <= bufferBytesRead)
                    {
                        PDU = buffer.AsSpan(pointer, PDULength);
                        pointer += (ushort)(PDULength);
                    }
                    else
                    {
                        fragmented = true;
                        //Проверка если MBAP не был фрагментирован
                        if (bytesReaded == 0) bytesReaded = 7;
                        bytesReaded += (ushort)(bufferBytesRead - pointer);
                        message.Write(buffer.AsSpan(pointer, bufferBytesRead - pointer));
                        break;
                    }
                    #endregion

                    //Проверяю что мы этот запрос отправляли
                    if (!_activeTransactions.ContainsKey(localTransactionID)
                        || !_activeTransactions[localTransactionID])
                    {
                        Clear();
                        continue;
                    }
                    else
                    {
                        _activeTransactions.TryRemove(localTransactionID, out _);
                    }

                    switch (PDU[0])
                    {
                        case 0x03:
                            localRegisterLength = (ushort)_deviceRegisters.GetLength(address);

                            if (PDU[1] == PDU.Length - 2
                                && PDU[1] % 2 == 0
                                && PDU[1] <= 2 * localRegisterLength
                                && PDU[1] > 4)
                            {
                                //Использую существую переменную, чтобы не плодить новые переменные
                                bytesReaded = BinaryPrimitives.ReadUInt16BigEndian(PDU.Slice(2, 2));

                                if (localRegisterLength != bytesReaded)
                                    _ = TrySetActualLengthRequest(stream, address, cancellationToken)
                                    // Пока не придумал что делать в случае ошибки
                                    .ContinueWith(t => { if (t.IsFaulted || !t.Result) ; }, TaskContinuationOptions.ExecuteSynchronously);

                                registers = registersArray
                                    .AsSpan(0, localRegisterLength);

                                registersBigEndian = MemoryMarshal.Cast<byte, ushort>(
                                    PDU.Slice(2)
                                );

                                BinaryPrimitives.ReverseEndianness(registersBigEndian, registers);
                                _deviceRegisters.TrySetArray(address, registers);
                            }
                            else if (PDU[1] == PDU.Length - 2
                                && PDU[1] % 2 == 0
                                && PDU[1] == 2)
                            {
                                //Использую существую переменную, чтобы не плодить новые переменные
                                bytesReaded = BinaryPrimitives.ReadUInt16BigEndian(PDU.Slice(2, 2));
                                _requestResponseReceived[localTransactionID].SetResult(bytesReaded);
                            }
                            Clear();
                            break;
                        case 0x06:
                            _commandResponseReceived[localTransactionID].SetResult();
                            break;
                        case 0x83:
                            if (PDU[1] == 0x02)
                            {
                                _ = TrySetActualLengthRequest(stream, address, cancellationToken)
                                    // Пока не придумал что делать в случае ошибки
                                    .ContinueWith(t => { if (t.IsFaulted || !t.Result) ; }, 
                                    TaskContinuationOptions.ExecuteSynchronously);
                            }
                            Clear();
                            continue;
                        default:
                            Clear();
                            continue;
                    }
                    Clear();
                }
            }

            void Clear()
            {
                bytesReaded = 0;
                fragmented = false;
                message.SetLength(0);
                message.Position = 0;
            }
        }

        #endregion

        #region Подключение 

        public async Task SearchConnections(
            CancellationToken cancellationToken = default)
        {
            HashSet<IPAddress> existingAddress = new HashSet<IPAddress>();

            foreach (var connection in _clients)
            {
                if (connection.Key.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                {
                    existingAddress.Add(remoteEndPoint.Address);
                }
            }

            Task[] tcpClientsTasks = new Task[16];

            for (int i = 0; i < 16; i++)
            {

                tcpClientsTasks = new Task[16];

                for (int j = 0; j < 16; j++)
                {
                    IPAddress address = IPAddress.Parse($"10.1.1.{i * 16 + j}");
                    if (!existingAddress.Contains(address))
                    {
                        tcpClientsTasks[j] = TryConnect(new(address, 502), cancellationToken);
                    }
                }

                await Task.WhenAll(tcpClientsTasks).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }

        public async Task TryConnect(
            IPEndPoint Ip,
            CancellationToken cancellationToken = default)
        {
            TcpClient tcpClient = new TcpClient();
            try
            {
                using CancellationTokenSource timeCts = new(
                        TimeSpan.FromSeconds(2));

                using CancellationTokenSource cts
                    = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeCts.Token);

                await tcpClient.ConnectAsync(Ip, cts.Token).ConfigureAwait(false);

                var stream = tcpClient.GetStream();

                ushort length = await TryGetLengthRequest(
                    stream,
                    Ip.Address,
                    cts.Token
                );

                if (length == 0)
                {
                    return;
                }

                if (tcpClient != null && tcpClient != null)
                {
                    _clients.TryAdd(tcpClient, stream);
                    var endPoint = tcpClient.Client.RemoteEndPoint;
                    if (endPoint is IPEndPoint ip)
                        _deviceRegisters.TryAdd(ip.Address, new ushort[length]);
                }

                if (_work)
                {
                    _tasks.Add(Task.Run(()
                        => ReadResponceLoop(stream, _cancellationTokenSource.Token)));
                }
            }
            catch (Exception)
            {
                tcpClient.Dispose();
            }
        }

        #endregion

        private void TryUpdateStreamsDictionary()
        {
            if (_streams.Count == _clients.Count)
                return;

            foreach (var client in _clients)
            {
                EndPoint? endpoint = client.Key.Client.RemoteEndPoint;

                if (endpoint != null
                    && endpoint is IPEndPoint ip
                    && !_streams.ContainsKey(ip.Address))
                {
                    _streams.TryAdd(ip.Address, client.Value);
                }
            }
        }

        private IPAddress? GetIp(TcpClient client)
        {
            var endPoint = client.Client.RemoteEndPoint;
            if (endPoint is IPEndPoint ip)
                return ip.Address;
            else return null;
        }

        public void Dispose()
        {
            Stop();

            foreach (var client in _clients)
            {
                client.Value.Dispose();
                client.Key.Dispose();
            }

            var clients = Interlocked.Exchange(ref _clients!, null);
            clients.Clear();

            var deviceRegisters = Interlocked.Exchange(ref _deviceRegisters!, null);
            deviceRegisters.Dispose();

            var activeTransactions = Interlocked.Exchange(ref _activeTransactions!, null);
            activeTransactions.Clear();
        }
    }

    public class NoIPExeption : Exception
    { }
}
