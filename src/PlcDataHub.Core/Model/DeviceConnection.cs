namespace PlcDataHub.Core.Model;

/// <summary>
/// 设备连接。规格 3.2 节 cfg.device_connection 的内存表示。
/// 只承载数据，不含行为。
/// </summary>
/// <param name="ConnId">连接主键</param>
/// <param name="ConnCode">设备代码，仅 [a-z0-9_]，用于生成表名</param>
/// <param name="ConnName">显示名，可中文</param>
/// <param name="Protocol">协议类型</param>
/// <param name="Host">IP 或主机名。S7 / Modbus TCP 必填</param>
/// <param name="Port">端口</param>
/// <param name="Rack">S7 机架号</param>
/// <param name="Slot">S7 槽号</param>
/// <param name="SerialPort">Modbus RTU 串口名，如 COM3</param>
/// <param name="Baud">Modbus RTU 波特率</param>
/// <param name="Enabled">是否启用</param>
public sealed record DeviceConnection(
    int ConnId,
    string ConnCode,
    string ConnName,
    ProtocolKind Protocol,
    string? Host,
    int Port,
    int Rack,
    int Slot,
    string? SerialPort,
    int Baud,
    bool Enabled)
{
    /// <summary>
    /// 返回指定协议的默认端口。S7 为 102，Modbus TCP 为 502。
    /// <para>
    /// <see cref="ProtocolKind.ModbusRtu"/> 返回 <c>0</c>，语义是<b>"不适用"</b>
    /// （RTU 走串口，本来就没有 TCP 端口），<b>不是</b>"任意端口"
    /// （<c>0</c> 在 TCP 语义里表示"由系统分配"）。
    /// 落库与连接层必须把 RTU 的端口视为无意义值，不得据此发起 TCP 连接。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocol"/> 不是已定义的协议值。</exception>
    public static int DefaultPortFor(ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.S7 => 102,
        ProtocolKind.ModbusTcp => 502,
        ProtocolKind.ModbusRtu => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "未知协议"),
    };
}
