using System.Net.Sockets;
using NModbus;

namespace PlcDataHub.Protocols.Modbus;

/// <summary>
/// Modbus 请求通道：把"读功能码 01/02/03/04"收敛成四个同步方法。
/// </summary>
/// <remarks>
/// <para>
/// 存在的唯一理由是**可测性**：<see cref="ModbusTcpConnection"/> 的编排逻辑
/// （按 (从站号, 寄存器区) 分组、位区/寄存器区两条读取路径、逐块故障隔离、地址校验）
/// 若只能经真实 socket 才能触发，几十条断言就都要依赖网络与端口。
/// </para>
/// <para>
/// 接缝刻意做得很小（四个方法 + <see cref="IDisposable"/>），生产路径上唯一的实现是
/// <see cref="NModbusRequestChannel"/>——它只是 NModbus 同名方法的转发，没有任何逻辑。
/// <b>真实链路（NModbus + socket + 线路字节）由 <c>ModbusTcpWireTests</c> 用手写的最小
/// Modbus TCP 从站端到端覆盖</b>，所以"这个接缝被验证过"不等于"只有这个接缝被验证过"。
/// </para>
/// </remarks>
internal interface IModbusRequestChannel : IDisposable
{
    /// <summary>功能码 01：读线圈。</summary>
    bool[] ReadCoils(byte slaveId, ushort startAddress, ushort quantity);

    /// <summary>功能码 02：读离散输入。</summary>
    bool[] ReadInputs(byte slaveId, ushort startAddress, ushort quantity);

    /// <summary>功能码 03：读保持寄存器。</summary>
    ushort[] ReadHoldingRegisters(byte slaveId, ushort startAddress, ushort quantity);

    /// <summary>功能码 04：读输入寄存器。</summary>
    ushort[] ReadInputRegisters(byte slaveId, ushort startAddress, ushort quantity);
}

/// <summary>NModbus 的通道实现：纯转发。同步读（NModbus 的同步 API 会阻塞到响应或超时）。</summary>
internal sealed class NModbusRequestChannel : IModbusRequestChannel
{
    private readonly IModbusMaster _master;
    private readonly TcpClient _client;

    internal NModbusRequestChannel(IModbusMaster master, TcpClient client)
    {
        _master = master;
        _client = client;
    }

    public bool[] ReadCoils(byte slaveId, ushort startAddress, ushort quantity) =>
        _master.ReadCoils(slaveId, startAddress, quantity);

    public bool[] ReadInputs(byte slaveId, ushort startAddress, ushort quantity) =>
        _master.ReadInputs(slaveId, startAddress, quantity);

    public ushort[] ReadHoldingRegisters(byte slaveId, ushort startAddress, ushort quantity) =>
        _master.ReadHoldingRegisters(slaveId, startAddress, quantity);

    public ushort[] ReadInputRegisters(byte slaveId, ushort startAddress, ushort quantity) =>
        _master.ReadInputRegisters(slaveId, startAddress, quantity);

    public void Dispose()
    {
        // master 会连带释放 transport 与流；TcpClient 再显式释放一次（重复释放是安全的），
        // 以免 NModbus 将来改了内部释放行为后留下一个仍占着 fd 的 socket。
        try
        {
            _master.Dispose();
        }
        finally
        {
            _client.Dispose();
        }
    }
}
