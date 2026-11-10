using System.Net;

namespace ModBusV2
{
    public class UI
    {
        public async Task Start()
        {
            bool isCancellationRequested = false;

            using ModBusCollector collector = new();

            collector.Start();

            while (!isCancellationRequested)
            {
                var connectionText = """
            1 - Ручное подключение устройства
            2 - Поиск доступных устройств
            3 - Пропустить
            """;

                switch (GetNumberFromConsole(3, connectionText))
                {
                    case 0:
                        isCancellationRequested = true;
                        continue;
                    case 1:
                        IPEndPoint ip = GetIPEndPointFromConsole();
                        await UseWithLoading("Подключение", async () =>
                        {
                            await collector.TryConnect(ip);
                        });
                        break;
                    case 2:
                        await UseWithLoading("Поиск устройств", async () =>
                        {
                            await collector.SearchConnections();
                        });
                        break;
                    case 3:
                        break;
                }

                if (collector.Devices.Count == 0)
                {
                    Console.Clear();
                    Console.WriteLine("Нет доступных устройств, перезапуск поиска");
                    await Task.Delay(1000);
                    continue;
                }

                Console.Clear();

                int startTop = Console.CursorTop;
                DateTime nextUpdate = DateTime.MinValue;
                string currentInputString = "";

                while (!isCancellationRequested)
                {
                    List<IPAddress> iPs = collector.Devices.ToList();

                    if (DateTime.UtcNow >= nextUpdate)
                    {
                        RefreshDevices(
                            iPs,
                            collector,
                            ref nextUpdate,
                            startTop,
                            currentInputString);
                    }

                    if (Console.KeyAvailable)
                    {
                        if (!HandleKeyInput(iPs, collector, ref nextUpdate, ref startTop, ref currentInputString))
                            break;
                    }

                    await Task.Delay(50);
                }
            }
        }

        private async Task HandleDeviceWrite(ModBusCollector collector, IPAddress ipAddress)
        {
            ushort register = 0;
            while (true)
            {
                Console.Write("Введите номер регистра (ushort): ");
                if (ushort.TryParse(Console.ReadLine(), out register)) break;
                Console.WriteLine("Некорректный формат. Попробуйте еще раз.");
            }

            ushort value = 0;
            while (true)
            {
                Console.Write("Введите значение для записи (ushort): ");
                if (ushort.TryParse(Console.ReadLine(), out value)) break;
                Console.WriteLine("Некорректный формат. Попробуйте еще раз.");
            }

            bool success = false;

            await UseWithLoading($"Отправка запроса: Регистр {register} = {value}", async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                success = await collector.TrySendWriteCommand(ipAddress, register, value, cts.Token);
            });

            if (success)
            {
                Console.WriteLine("Данные успешно отправлены!");
            }
            else
            {
                Console.WriteLine("Ошибка при отправке данных.");
            }

            Console.WriteLine("Нажмите любую клавишу для возврата к списку...");
            Console.ReadKey(true);
        }

        private bool HandleKeyInput(
            List<IPAddress> iPs,
            ModBusCollector collector,
            ref DateTime nextUpdate,
            ref int startTop,
            ref string currentInputString)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                if (int.TryParse(currentInputString, out int choice))
                {
                    int backIndex = iPs.Count + 1;
                    if (choice == backIndex)
                    {
                        Console.Clear();
                        return false;
                    }
                    else if (choice > 0 && choice <= iPs.Count)
                    {
                        var registersText = "";
                        var targetIp = iPs[choice - 1];
                        if (collector.DeviceRegisters.TryGetValue(targetIp, out var registers)
                            && registers != null
                            && registers[0] != 0)
                        {
                            registersText = string.Join(" ", registers);
                        }
                        Console.Clear();
                        Console.WriteLine(
                            $"=== Управление устройством " +
                            $"{targetIp.MapToIPv4()} === \n[Регистры: {registersText}]");
                        HandleDeviceWrite(collector, targetIp).Wait();

                        Console.Clear();
                        startTop = Console.CursorTop;
                        currentInputString = "";
                        nextUpdate = DateTime.MinValue;
                        return true;
                    }
                }
                currentInputString = "";
                nextUpdate = DateTime.MinValue;
            }
            else if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (currentInputString.Length > 0)
                {
                    currentInputString = currentInputString[..^1];
                    Console.Write("\b \b");
                }
            }
            else if (char.IsDigit(keyInfo.KeyChar))
            {
                currentInputString += keyInfo.KeyChar;
                Console.Write(keyInfo.KeyChar);
            }

            return true;
        }

        private void RefreshDevices(
            List<IPAddress> iPs,
            ModBusCollector collector,
            ref DateTime nextUpdate,
            int startTop,
            string currentInputString)
        {
            Console.SetCursorPosition(0, startTop);
            Console.WriteLine("Доступные устройства:" + new string(' ', 20));

            int i = 0;
            foreach (var address in iPs)
            {
                i++;
                string registersText = "нет данных";
                if (collector.DeviceRegisters.TryGetValue(address, out var registers)
                    && registers != null
                    && registers[0] != 0)
                {
                    registersText = string.Join(" ", registers);
                }

                string line = $"{i} - {address.MapToIPv4()} [Регистры: {registersText}]";
                Console.WriteLine(line.PadRight(Console.WindowWidth - 1));
            }

            Console.WriteLine($"{++i} - Назад".PadRight(Console.WindowWidth - 1));
            Console.WriteLine(new string('-', 30).PadRight(Console.WindowWidth - 1));

            Console.Write($"Выберите устройство: {currentInputString}");

            nextUpdate = DateTime.UtcNow.AddSeconds(3);
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

        private async Task UseWithLoading(string text, Func<Task> action)
        {
            using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

            var loading = Task.Run(action.Invoke)
                .ContinueWith(t => cancellationTokenSource.Cancel(), 
                              TaskContinuationOptions.ExecuteSynchronously);

            try
            {
                await WriteLoading(text, cancellationTokenSource.Token);                
            }
            catch (OperationCanceledException) 
            {

            }

            await loading;
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
                    await Task.Delay(250, cancellationToken);
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
