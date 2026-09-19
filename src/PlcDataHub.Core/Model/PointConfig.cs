namespace PlcDataHub.Core.Model;

/// <summary>S7 地址。规格 3.2 节 s7_area / s7_db / s7_byte / s7_bit。</summary>
/// <param name="Area">区域 I/Q/M/DB</param>
/// <param name="Db">DB 号。S7-200 SMART 的 V 区 = DB1</param>
/// <param name="ByteOffset">字节偏移</param>
/// <param name="BitOffset">位偏移 0~7，仅 BOOL 使用</param>
public sealed record S7Address(S7Area Area, int Db, int ByteOffset, int BitOffset);

/// <summary>Modbus 地址。规格 3.2 节 mb_slave / mb_reg_area / mb_address。</summary>
/// <param name="SlaveId">从站号 1~247</param>
/// <param name="Area">寄存器区</param>
/// <param name="RegisterAddress">寄存器地址，0 基</param>
public sealed record ModbusAddress(int SlaveId, ModbusRegisterArea Area, int RegisterAddress);

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
