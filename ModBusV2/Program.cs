namespace ModBusV2
{
    internal class Program
    {
        static void Main(string[] args)
        {
            UI uI = new UI();
            uI.Start().GetAwaiter().GetResult();
        }

    }
}
