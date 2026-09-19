namespace PlcDataHub.Core.Model;

/// <summary>S7 地址。规格 3.2 节 s7_area / s7_db / s7_byte / s7_bit。</summary>
/// <param name="Area">区域 I/Q/M/DB</param>
/// <param name="Db">DB 号。S7-200 SMART 的 V 区 = DB1</param>
/// <param name="ByteOffset">字节偏移</param>
/// <param name="BitOffset">位偏移 0~7，仅 BOOL 使用</param>
public sealed record S7Address(S7Area Area, int Db, int ByteOffset, int BitOffset);

/// <summary>Modbus 地址。规格 3.2 节 mb_slave / mb_reg_area / mb_address。</summary>
/// <param name="SlaveId">
/// 从站号，**合法范围 1~247**（规格 3.2 节）。0 是广播地址、248~255 保留，都不能用于读取：
/// 它们会被编码成一个"看起来合法"的请求字节，**读回错从站的数据而不报错**。
/// 校验由连接层（ModbusTcpConnection）在发出任何请求之前完成并响亮抛异常。
/// </param>
/// <param name="Area">寄存器区。决定读取用的功能码（01/02/03/04），见 <see cref="ModbusRegisterArea"/>。</param>
/// <param name="RegisterAddress">
/// 地址，**0 基**（规格 3.2 节）。
/// <para>
/// ⚠️ **单位随 <paramref name="Area"/> 而变**：
/// <list type="bullet">
///   <item>寄存器区（<see cref="ModbusRegisterArea.HoldingRegister"/> / <see cref="ModbusRegisterArea.InputRegister"/>）：
///         它是 **16 位寄存器编号**。</item>
///   <item>位区（<see cref="ModbusRegisterArea.Coil"/> / <see cref="ModbusRegisterArea.DiscreteInput"/>）：
///         它是 **位编号**——一个线圈/离散输入就是一位，此时 <paramref name="BitOffset"/> 不参与寻址（必须为 0）。</item>
/// </list>
/// 两种编号空间**互不通用**：保持寄存器 100 与输入寄存器 100、线圈 100 是完全不同的对象。
/// </para>
/// </param>
/// <param name="BitOffset">
/// **寄存器内**的位偏移 0~15，仅当 <paramref name="Area"/> 是寄存器区、且点类型为
/// <see cref="PointDataType.Bool"/> 时有意义；默认 0 表示寄存器的最低位。
/// <para>
/// 位约定遵循 Modbus 惯例：**位 0 = 寄存器值的 bit 0（LSB）**，位 15 = bit 15（MSB）。
/// 连接层把寄存器按大端拆成字节流后，位 0~7 落在该寄存器的**低字节**、位 8~15 落在**高字节**
/// （映射与实测边界见 ModbusTcpConnection 的位路径说明与其测试）。
/// </para>
/// <para>
/// 本参数**带默认值 0**，因此 `new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)`
/// 这类既有构造点完全不受影响。位区（线圈/离散输入）不使用本字段——把它用在位区是配置错误，
/// 由连接层响亮拒绝，而不是被静默忽略。
/// </para>
/// </param>
public sealed record ModbusAddress(
    int SlaveId,
    ModbusRegisterArea Area,
    int RegisterAddress,
    int BitOffset = 0);

/// <summary>
/// 采集点。规格 3.2 节 cfg.point 的内存表示。
/// S7 与 Modbus 地址字段二者只会有其一非空，取决于所属连接的协议。
/// </summary>
public sealed record PointConfig(
    int PointId,
    string PointCode,
    string PointName,
    string ColumnName,
    PointDataType DataType,
    ByteOrder ByteOrder,
    double Scale,
    double Offset,
    bool Enabled,
    S7Address? S7,
    ModbusAddress? Modbus)
{
    /// <summary>
    /// 把原始值转换为工程值：raw * Scale + Offset。
    /// 规格 3.4 节：落库前应用，库里存工程值。
    /// </summary>
    public double ToEngineeringValue(double raw) => raw * Scale + Offset;
}
