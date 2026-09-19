using PlcDataHub.Core.Model;
using PlcDataHub.Protocols.Modbus;

namespace PlcDataHub.Protocols.Tests;

/// <summary>
/// <see cref="ModbusTcpConnection"/> 的测试桩：记录每次请求，按 (从站号, 寄存器区, 起始地址) 提供数据。
/// </summary>
/// <remarks>
/// <para>
/// <b>没配置数据的请求会抛异常</b>，而不是安静地返回 0/空。
/// 这一条是刻意的：若桩对未配置的请求返回空数组，测试里的"点位是坏值"就同时可能来自
/// "连接层逻辑错"和"我忘了配数据"，断言会变成假绿。抛异常让两种失败立刻分开。
/// </para>
/// <para>
/// 它记录 <b>原始请求</b>（从站号/区/起始地址/长度），因为本项目最危险的失效是
/// "请求发给了错的从站/错的区/错的长度"——那种错误不会抛异常，只会读回错值。
/// </para>
/// </remarks>
internal sealed class StubModbusChannel : IModbusRequestChannel
{
    private readonly Dictionary<(byte SlaveId, ModbusRegisterArea Area, ushort Start), ushort[]> _registers = new();
    private readonly Dictionary<(byte SlaveId, ModbusRegisterArea Area, ushort Start), bool[]> _bits = new();
    private readonly Dictionary<(byte SlaveId, ModbusRegisterArea Area), Func<Exception>> _failures = new();

    internal List<ModbusCall> Calls { get; } = new();

    internal int DisposeCount { get; private set; }

    internal void SetRegisters(byte slaveId, ModbusRegisterArea area, ushort start, params ushort[] values) =>
        _registers[(slaveId, area, start)] = values;

    internal void SetBits(byte slaveId, ModbusRegisterArea area, ushort start, params bool[] values) =>
        _bits[(slaveId, area, start)] = values;

    /// <summary>让该 (从站, 区) 的每次读取都抛出给定异常（每次调用重新构造，便于断言异常类型）。</summary>
    internal void FailWith(byte slaveId, ModbusRegisterArea area, Func<Exception> exceptionFactory) =>
        _failures[(slaveId, area)] = exceptionFactory;

    public bool[] ReadCoils(byte slaveId, ushort startAddress, ushort quantity) =>
        ReadBits(slaveId, ModbusRegisterArea.Coil, startAddress, quantity);

    public bool[] ReadInputs(byte slaveId, ushort startAddress, ushort quantity) =>
        ReadBits(slaveId, ModbusRegisterArea.DiscreteInput, startAddress, quantity);

    public ushort[] ReadHoldingRegisters(byte slaveId, ushort startAddress, ushort quantity) =>
        ReadRegisters(slaveId, ModbusRegisterArea.HoldingRegister, startAddress, quantity);

    public ushort[] ReadInputRegisters(byte slaveId, ushort startAddress, ushort quantity) =>
        ReadRegisters(slaveId, ModbusRegisterArea.InputRegister, startAddress, quantity);

    public void Dispose() => DisposeCount++;

    private ushort[] ReadRegisters(byte slaveId, ModbusRegisterArea area, ushort startAddress, ushort quantity)
    {
        Record(slaveId, area, startAddress, quantity);

        if (!_registers.TryGetValue((slaveId, area, startAddress), out var values) || values.Length < quantity)
        {
            throw new InvalidOperationException(
                $"测试桩没有为 (从站 {slaveId}, {area}, 起始 {startAddress}, 长度 {quantity}) 配置寄存器数据");
        }

        return values.Take(quantity).ToArray();
    }

    private bool[] ReadBits(byte slaveId, ModbusRegisterArea area, ushort startAddress, ushort quantity)
    {
        Record(slaveId, area, startAddress, quantity);

        if (!_bits.TryGetValue((slaveId, area, startAddress), out var values) || values.Length < quantity)
        {
            throw new InvalidOperationException(
                $"测试桩没有为 (从站 {slaveId}, {area}, 起始 {startAddress}, 长度 {quantity}) 配置位数据");
        }

        return values.Take(quantity).ToArray();
    }

    private void Record(byte slaveId, ModbusRegisterArea area, ushort startAddress, ushort quantity)
    {
        Calls.Add(new ModbusCall(slaveId, area, startAddress, quantity));

        if (_failures.TryGetValue((slaveId, area), out var factory))
        {
            throw factory();
        }
    }
}

/// <summary>一次被记录的 Modbus 请求。</summary>
internal sealed record ModbusCall(byte SlaveId, ModbusRegisterArea Area, ushort StartAddress, ushort Quantity);
