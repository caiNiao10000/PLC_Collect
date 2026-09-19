namespace PlcDataHub.Protocols.Modbus;

/// <summary>
/// 寄存器内的位（Modbus 惯例：**位 0 = 寄存器值的 bit 0，即 LSB**）→ 大端字节流中的位置。
/// </summary>
/// <remarks>
/// <para>
/// 连接层把每个 16 位寄存器按大端拆成两个字节（高字节在前）：
/// <code>
/// registers[i] = 0x0102  →  bytes[2i] = 0x01（高字节）, bytes[2i+1] = 0x02（低字节）
/// </code>
/// 于是寄存器位 0~7 落在**低字节**、位 8~15 落在**高字节**：
/// <code>
/// 位  0 → 低字节的 bit 0        位  8 → 高字节的 bit 0
/// 位  7 → 低字节的 bit 7        位 15 → 高字节的 bit 7
/// </code>
/// </para>
/// <para>
/// ⚠️ 这一步极易反。若把位 0 映射到高字节，寄存器 0x0001 的位 0 会读成 <c>false</c>（而不是 <c>true</c>），
/// 而且**不抛异常、不返回坏值**——正是规格 9 节那类"数据是错的但不会报错"的失效。
/// 映射由两组用例正反夹住：`同一个保持寄存器内的不同位各取各的位`（高/低字节各取一位）
/// 与 `寄存器位偏移映射到大端字节位置`（位 0 / 7 / 8 / 15 逐个断言）。
/// </para>
/// <para>
/// 本类同时被 <see cref="ModbusTcpConnection"/>（寄存器合并块内的偏移）与
/// <c>PlcDataHub.Protocols.Fake.FakePlcConnection</c>（点自己的 2 字节缓冲区）使用，
/// 以保证"假连接解出来的位"与"真实连接解出来的位"是同一套语义。
/// </para>
/// </remarks>
internal static class ModbusRegisterBits
{
    /// <summary>
    /// 寄存器内位偏移 0~15 落在该寄存器两个大端字节中的哪一个：
    /// <c>1</c> = 低字节（位 0~7），<c>0</c> = 高字节（位 8~15）。
    /// </summary>
    internal static int ByteIndexForBit(int bitOffset) => bitOffset < 8 ? 1 : 0;
}
