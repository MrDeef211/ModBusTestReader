namespace ModBusV2
{
    public class ConcurentArrayListOfArray<T>
    {
        private const ushort NumberOfRegisters = 10;

        ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        List<T[]> arrays = new List<T[]>();
    }
}
