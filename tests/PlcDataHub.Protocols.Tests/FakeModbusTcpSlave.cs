using System.Net;
using System.Net.Sockets;

namespace PlcDataHub.Protocols.Tests;

/// <summary>
/// 手写的**最小 Modbus TCP 从站**（仅测试用，不进生产代码）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要自己写一个而不是用 NModbus 自带的从站：本任务要确认的问题正是
/// <b>"NModbus 客户端把线路字节解释成了什么"</b>。若从站也用 NModbus，
/// 两端同源，确认不了任何东西。这里的响应字节是**按 Modbus 规范手工拼**的
/// （MBAP 7 字节 + PDU；功能码 01/02 的数据按"每 8 位打包 1 字节、LSB 对应最低地址"），
/// 于是"客户端解出来的位/值"与"我们按规范写进去的位/值"是两个独立来源。
/// </para>
/// <para>
/// 覆盖范围只到本任务用到的功能码（01/02/03/04），不做写、不做 RTU、不做 CRC（TCP 不需要）。
/// 每个请求都被记录（含**收到的原始 PDU 字节**与**写回的原始 PDU 字节**），
/// 使测试能同时断言"发了什么"和"回了什么"，而不是只断言最终数值。
/// </para>
/// </remarks>
internal sealed class FakeModbusTcpSlave : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly List<ModbusWireRequest> _requests = new();
    private readonly Dictionary<byte, FakeSlaveMemory> _slaves = new();
    private readonly Dictionary<byte, byte> _exceptionCodeByUnitId = new();
    private readonly Task _acceptLoop;

    internal FakeModbusTcpSlave()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>从站监听的端口（由系统分配，测试把它交给被测连接）。</summary>
    internal int Port { get; }

    /// <summary>收到的请求，按到达顺序。</summary>
    internal IReadOnlyList<ModbusWireRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>最后一次写回的 PDU 原始字节（用于断言"我们按规范写了什么"）。</summary>
    internal byte[]? LastResponsePdu { get; private set; }

    /// <summary>为 true 时只收不回（用于构造"读到超时"的真实场景）。</summary>
    internal bool Mute { get; set; }

    /// <summary>
    /// 收到第 N 个请求时**放弃式关闭**这条连接（在写回响应之前，发 RST），
    /// 用于构造"设备在采集过程中消失 / 网线被拔"。0（默认）= 不中止。
    /// <para>
    /// 选"收到第 N 个请求就 RST"而不是"写回第 N 个响应后再 RST"，是为了**确定性**：
    /// 后者与客户端的读取存在竞争（响应可能已进内核缓冲区、也可能已被 RST 丢弃），
    /// 测试会时而"这次读成功"、时而"这次读失败"。前者保证"第 N-1 次读一定成功、第 N 次一定失败"。
    /// </para>
    /// </summary>
    internal int AbortOnRequestNumber { get; set; }

    /// <summary>取（必要时创建）某个从站号的内存区。</summary>
    internal FakeSlaveMemory Slave(byte unitId)
    {
        lock (_sync)
        {
            if (!_slaves.TryGetValue(unitId, out var memory))
            {
                memory = new FakeSlaveMemory();
                _slaves[unitId] = memory;
            }

            return memory;
        }
    }

    /// <summary>让该从站号对任何请求都回异常码（PDU：功能码 | 0x80，异常码）。</summary>
    internal void RespondWithException(byte unitId, byte exceptionCode)
    {
        lock (_sync)
        {
            _exceptionCodeByUnitId[unitId] = exceptionCode;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 取消导致的异常在收尾时无意义。
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];

            // 本次连接已收到的请求数（AbortOnRequestNumber 用；按连接计数，不看全局）。
            var requestsReceived = 0;

            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header))
                {
                    return;
                }

                var transactionId = (ushort)((header[0] << 8) | header[1]);
                var protocolId = (ushort)((header[2] << 8) | header[3]);
                var length = (ushort)((header[4] << 8) | header[5]);
                var unitId = header[6];

                // MBAP 的 length 含 unitId 本身，故 PDU 长度 = length - 1。
                var pdu = new byte[length - 1];

                if (pdu.Length > 0 && !await ReadExactAsync(stream, pdu))
                {
                    return;
                }

                if (Mute)
                {
                    // 收了不回：让客户端走到 Transport.ReadTimeout。
                    continue;
                }

                var response = BuildResponse(transactionId, protocolId, unitId, pdu);

                if (AbortOnRequestNumber > 0 && ++requestsReceived >= AbortOnRequestNumber)
                {
                    // 收到第 N 个请求就消失：**先记录请求（BuildResponse 里已记）、不回响应、直接 RST**。
                    // 这是"设备突然消失/网线被拔"最接近的本机形态，而且没有竞态（见属性注释）。
                    Abort(client);
                    return;
                }

                await stream.WriteAsync(response, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
    }

    /// <summary>放弃式关闭：SO_LINGER(timeout 0) + Close 会发 RST 而不是正常的 FIN。</summary>
    private static void Abort(TcpClient client)
    {
        try
        {
            client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
            client.Client.Close();
        }
        catch (SocketException)
        {
            // 对端已经先关了：目的（让这条连接消失）已经达到。
        }
        catch (ObjectDisposedException)
        {
            // 同上。
        }
    }

    private async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(offset), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private byte[] BuildResponse(ushort transactionId, ushort protocolId, byte unitId, byte[] pdu)
    {
        var functionCode = pdu[0];
        var startAddress = (ushort)((pdu[1] << 8) | pdu[2]);
        var quantity = (ushort)((pdu[3] << 8) | pdu[4]);

        var memory = Slave(unitId);
        byte[] responsePdu;

        lock (_sync)
        {
            _requests.Add(new ModbusWireRequest(unitId, functionCode, startAddress, quantity, pdu));

            responsePdu = _exceptionCodeByUnitId.TryGetValue(unitId, out var exceptionCode)
                ? [(byte)(functionCode | 0x80), exceptionCode]
                : functionCode switch
                {
                    1 => BitsResponse(1, memory.Coils, startAddress, quantity),
                    2 => BitsResponse(2, memory.DiscreteInputs, startAddress, quantity),
                    3 => RegistersResponse(3, memory.HoldingRegisters, startAddress, quantity),
                    4 => RegistersResponse(4, memory.InputRegisters, startAddress, quantity),
                    _ => [(byte)(functionCode | 0x80), 0x01], // 非法功能码
                };

            LastResponsePdu = responsePdu;
        }

        var frame = new byte[7 + responsePdu.Length];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)(transactionId & 0xFF);
        frame[2] = (byte)(protocolId >> 8);
        frame[3] = (byte)(protocolId & 0xFF);

        var length = (ushort)(1 + responsePdu.Length);
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)(length & 0xFF);
        frame[6] = unitId;
        responsePdu.CopyTo(frame, 7);

        return frame;
    }

    /// <summary>
    /// 位响应（功能码 01/02）。**打包规则是 Modbus 规范**：每 8 个位打包进 1 字节，
    /// <b>LSB 对应最低地址</b>（第 i 个请求位 → 第 i/8 字节的第 i%8 位）。
    /// </summary>
    private static byte[] BitsResponse(byte functionCode, bool[] bits, ushort startAddress, ushort quantity)
    {
        var byteCount = (quantity + 7) / 8;
        var data = new byte[byteCount];

        for (var i = 0; i < quantity; i++)
        {
            if (bits[startAddress + i])
            {
                data[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        var pdu = new byte[2 + byteCount];
        pdu[0] = functionCode;
        pdu[1] = (byte)byteCount;
        data.CopyTo(pdu, 2);

        return pdu;
    }

    /// <summary>寄存器响应（功能码 03/04）：每个寄存器 2 字节、大端。</summary>
    private static byte[] RegistersResponse(byte functionCode, ushort[] registers, ushort startAddress, ushort quantity)
    {
        var pdu = new byte[2 + (quantity * 2)];
        pdu[0] = functionCode;
        pdu[1] = (byte)(quantity * 2);

        for (var i = 0; i < quantity; i++)
        {
            pdu[2 + (i * 2)] = (byte)(registers[startAddress + i] >> 8);
            pdu[3 + (i * 2)] = (byte)(registers[startAddress + i] & 0xFF);
        }

        return pdu;
    }
}

/// <summary>一个从站号的地址空间（按 Modbus 的 16 位地址直接索引）。</summary>
internal sealed class FakeSlaveMemory
{
    internal ushort[] HoldingRegisters { get; } = new ushort[65536];

    internal ushort[] InputRegisters { get; } = new ushort[65536];

    internal bool[] Coils { get; } = new bool[65536];

    internal bool[] DiscreteInputs { get; } = new bool[65536];
}

/// <summary>从站收到的一次请求（含原始 PDU 字节）。</summary>
internal sealed record ModbusWireRequest(byte UnitId, byte FunctionCode, ushort StartAddress, ushort Quantity, byte[] Pdu);
