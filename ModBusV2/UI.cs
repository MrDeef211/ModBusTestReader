using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ModBusV2
{
    public class UI
    {
        bool isCancellationRequested = false;

        public void Start()
        {
            while (!isCancellationRequested)
            {

            }
        }

        private int GetNumberFromConsole(
            int maxOption,
            string optionsText,
            string displayText = "Выберите нужный пункт:")
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine(optionsText);
                Console.WriteLine("Ex - Завершить програму\n");
                Console.WriteLine(displayText);
                string? input = Console.ReadLine();
                if (input == "Ex")
                {
                    return 0;
                }
                if (input == null || input == "")
                {
                    Console.WriteLine("Не удалось считать ввод");
                    Thread.Sleep(1000);
                    continue;
                }
                if (!int.TryParse(input, out var number))
                {
                    Console.WriteLine("Не удалось считать число");
                    Thread.Sleep(1000);
                    continue;
                }
                if (number <= 0 || number > maxOption)
                {
                    Console.WriteLine("Пункт не найден, введите другое число");
                    Thread.Sleep(1000);
                    continue;
                }
                return number;
            }
        }

        private IPEndPoint GetIPEndPointFromConsole()
        {
            while (true)
            {
                Console.Clear();
                Console.WriteLine("Введите IP");
                string? ip = Console.ReadLine();
                if (ip == null || ip == "")
                {
                    Console.WriteLine("Не удалось считать ввод");
                    Thread.Sleep(1000);
                    continue;
                }
                if (!IPAddress.TryParse(ip, out var ipAddress))
                {
                    Console.WriteLine("Не удалось получить IP");
                    Thread.Sleep(1000);
                    continue;
                }
                return new IPEndPoint(ipAddress, 502);
            }
        }

        private async Task WriteLoading(
            string text = "Загрузка",
            CancellationToken cancellationToken = default)
        {
            if (!cancellationToken.CanBeCanceled)
                cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

            int dotCount = 0;

            Console.Clear();

            while (!cancellationToken.IsCancellationRequested)
            {
                string dots = new string('.', dotCount);

                Console.Write($"\r{text}{dots}".PadRight(text.Length + 4));

                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                dotCount = (dotCount + 1) % 4;
            }
        }
    }
}
