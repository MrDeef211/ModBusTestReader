using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Tcp = (System.Net.Sockets.TcpClient Client, System.Net.Sockets.NetworkStream Stream);

namespace ModBusV2
{
    public class ModBusCollector : IDisposable
    {
        private CancellationTokenSource cancellationTokenSource;

        // Список активных клиентов + их стримы 
        private ConcurrentDictionary<TcpClient, Stream> _clients = new();

        // Кешированные данные регистров
        private ConcurrentDictionary<IPAddress, ushort[]> _deviceRegisters = new();

        // Актуальные для принятия транзакции
        private ConcurrentDictionary<ushort, bool> _activeTransactions = new();

        // ID текущей транзакции
        private ushort _transactionID = 0;

        // Флаг, работает ли сейчас опрос
        private bool _work = false;

        public IReadOnlyDictionary<IPAddress, ushort[]> DeviceRegisters => _deviceRegisters;

        public IReadOnlyCollection<IPAddress> Devices
            => (IReadOnlyCollection<IPAddress>)_deviceRegisters.Keys;

        public ModBusCollector()
        {

        }

        public void Start()
        {
            if (_work)
            {
                return;
            }

            cancellationTokenSource = new CancellationTokenSource();

            Task.Run(() => SendReadRequestLoop(cancellationTokenSource.Token));
            //Task.Run(() => ReadResponceLoop(cancellationTokenSource.Token));
            _work = true;
        }

        public void Stop()
        {
            if (!_work)
            {
                return;
            }

            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
            _work = false;
        }

        public bool TrySendWriteRequest(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        private async Task SendReadRequestLoop(CancellationToken cancellationToken = default)
        {
            ushort lenght = 6;
            ushort startIndex = 0;
            ushort indexCount = 10;
            ushort transactionID;
            byte command = 3;
            byte deviceId = 1;

            byte[] reqestData = new byte[lenght + 6];

            BinaryPrimitives.WriteUInt16BigEndian(reqestData.AsSpan(4, 2), lenght);
            reqestData[6] = deviceId;
            reqestData[7] = command;
            BinaryPrimitives.WriteUInt16BigEndian(reqestData.AsSpan(8, 2), startIndex);
            BinaryPrimitives.WriteUInt16BigEndian(reqestData.AsSpan(10, 2), indexCount);

            while (!cancellationToken.IsCancellationRequested)
            {
                //Опрос не чаще чем раз в 3 секунды
                var cooldown = Task.Delay(3000);

                foreach (var client in _clients)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    transactionID = ++_transactionID;
                    BinaryPrimitives.WriteUInt16BigEndian(reqestData.AsSpan(0, 2), transactionID);

                    await client.Value.WriteAsync(
                        reqestData,
                        cancellationToken)
                        .ConfigureAwait(false);

                    _activeTransactions.TryAdd(transactionID, true);
                }

                await cooldown.ConfigureAwait(false);
            }
        }

        private async Task ReadResponceLoop(NetworkStream stream, CancellationToken cancellationToken = default)
        {
            EndPoint? endpoint = stream.Socket.RemoteEndPoint;
            IPAddress address;

            if (endpoint is IPEndPoint ip)
                address = ip.Address;
            else throw new NoIPExeption();

            byte[] buffer = new byte[512];
            using MemoryStream message = new();

            Span<ushort> registers = new();
            ReadOnlySpan<ushort> registersBigEndian = new();
            ReadOnlySpan<byte> MBAP = new();
            ReadOnlySpan<byte> PDU = new();

            int bufferBytesRead = 0;
            ushort PDULength = 0;
            ushort pointer = 0;
            ushort bytesReaded = 0;
            ushort localTransactionID = 0;

            bool fragmented = false;

            while (!cancellationToken.IsCancellationRequested)
            {

                bufferBytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                registers = new();
                registersBigEndian = new();
                MBAP = new();
                PDU = new();

                if (bufferBytesRead == 0)
                {
                    // TODO Сделать разрыв соедениения
                }

                pointer = 0;

                while (pointer < bufferBytesRead && bufferBytesRead > 0 && !cancellationToken.IsCancellationRequested)
                {

                    // Если MBAP ещё не прочитан
                    if (bytesReaded < 7)
                    {
                        //Пытаюсь прочитать MBAP
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
                    }

                    if (!fragmented)
                        localTransactionID = BinaryPrimitives.ReadUInt16BigEndian(MBAP.Slice(0, 2));
                    else
                        localTransactionID = BinaryPrimitives.ReadUInt16BigEndian(
                            message
                            .GetBuffer()
                            .AsSpan(0, 2));

                    //Считаем длину PDU (Length хранит длину Unit ID + PDU)
                    if (!fragmented)
                        PDULength = BinaryPrimitives.ReadUInt16BigEndian(MBAP.Slice(4, 2));
                    else
                        PDULength = BinaryPrimitives.ReadUInt16BigEndian(
                            message
                            .GetBuffer()
                            .AsSpan(4, 2));

                    if (PDULength == 0)
                    {
                        Clear();
                        continue;
                    }

                    PDULength--;

                    //Пытаюсь прочитать PDU
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

                    //Проверяю что мы этот запрос отправляли
                    if (!_activeTransactions.ContainsKey(localTransactionID)
                        || _activeTransactions[localTransactionID])
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
                        case 3:
                            if (PDU.Length >= 3 && PDU.Length % 2 == 0)
                            {
                                registers = _deviceRegisters[address].AsSpan();
                                registersBigEndian = MemoryMarshal.Cast<byte, ushort>(PDU.Slice(2));
                                BinaryPrimitives.ReverseEndianness(registersBigEndian, registers);
                            }
                            Clear();
                            break;
                        case >= 0x80:
                            Clear();
                            continue;
                        default:
                            Clear();
                            continue;
                    }
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

        #region Подключение 

        private async Task SearchConnections(
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

                tcpClientsTasks = new Task<Tcp?>[16];

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

        private async Task TryConnect(
            IPEndPoint Ip,
            CancellationToken cancellationToken = default)
        {
            TcpClient tcpClient = new TcpClient();
            try
            {
                using CancellationTokenSource timeCts = new(
                        TimeSpan.FromSeconds(1));

                using CancellationTokenSource cts
                    = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, timeCts.Token);

                await tcpClient.ConnectAsync(Ip, cts.Token).ConfigureAwait(false);

                var stream = tcpClient.GetStream();

                // Если не пришло исключение, значит всё ок
                // TODO Нужно потом придумать нормальную индексацию
                //ushort test = await ReadRegister(
                //    stream,
                //    Ip.Address.GetAddressBytes()[3],
                //    0,
                //    cancellationToken);

                if (tcpClient != null && tcpClient != null)
                {
                    _clients.TryAdd(tcpClient, stream);
                    var endPoint = tcpClient.Client.RemoteEndPoint;
                    if (endPoint is IPEndPoint ip)
                        _deviceRegisters.TryAdd(ip.Address, new ushort[10]);
                }
            }
            catch (Exception)
            {
                tcpClient.Dispose();
            }
        }

        #endregion

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
            deviceRegisters.Clear();

            var activeTransactions = Interlocked.Exchange(ref _activeTransactions!, null);
            activeTransactions.Clear();
        }
    }

    public class NoIPExeption : Exception
    { }
}
