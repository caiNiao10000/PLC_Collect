using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols.Modbus;

/// <summary>
/// Modbus 点的**配置期**校验：在发出任何请求之前，把"这个配置永远不可能正确工作"的情况响亮拦下。
/// </summary>
/// <remarks>
/// <para>
/// 抽成独立类型（而不是留在 <c>ModbusTcpConnection</c> 里）是因为**假连接必须做同一套校验**：
/// <c>FakePlcConnection</c> 若对非法位偏移、非法从站号宽容，Plan 2 用假连接做的验证就会**假绿**
/// （同一份配置在假连接上"能跑"，到真实连接上却抛异常或静默错值）。共用这一处校验可以让两边不可能分叉。
/// </para>
/// <para>
/// **每一条规则都对应一条真实的静默失效路径**（不校验的后果都是"读到错值/永远坏值且没有信号"）：
/// <list type="number">
///   <item><b>从站号越界</b>：<c>(byte)slaveId</c> 直接截断时，0（广播）与 248~255
///         会被编码成一个看起来合法的请求字节，从站不会拒绝——于是读到别处的数据而不报错。</item>
///   <item><b>寄存器地址越界</b>：地址要经 <c>(ushort)</c> 落到请求里，超出 0~65535 会被截断，
///         请求打到完全不相干的寄存器上。</item>
///   <item><b>点的末寄存器越界</b>：地址合法但"地址 + 宽度 - 1"越过 65535 时，
///         块尾回绕，最后一个点读到块首附近的数据。</item>
///   <item><b>寄存器区未定义</b>：枚举被强转成未定义值时，功能码选择会落到某个默认分支。</item>
///   <item><b>位区里放了非 BOOL 点</b>：线圈/离散输入每个地址只有一位，WORD 在这里无法表达；
///         若按寄存器路径去读，会拿线圈的地址空间去发功能码 03/04——那是另一个编号空间。</item>
///   <item><b>位区里的 BitOffset 不为 0</b>：位区本身已按位寻址，位偏移无处可用；
///         静默忽略它等于"配置说取第 3 位、实际取了第 0 位"。</item>
///   <item><b>寄存器区 BOOL 的 BitOffset 越界</b>：不在 0~15 时位提取返回 null，
///         该点每个周期都是坏值且没有任何错误信号。</item>
///   <item><b>寄存器区里放了本期不支持的类型（Dtl / String）</b>：它们能通过规划（宽度 4 / 1 寄存器），
///         却在**解码期**才抛，而那时的异常发生在一次白发的请求之后、且不含点标识
///         （<c>ByteDecoder</c> 的入参里没有 <c>PointConfig</c>，结构上不可能点名）。故提前到这里。</item>
/// </list>
/// </para>
/// <para>
/// 异常分两类，Plan 2 的采集器若要按点隔离配置错误，catch 这两个类型即可：
/// <see cref="ArgumentOutOfRangeException"/>（取值问题，<c>ParamName</c> 一律是 <c>points</c>，
/// 与规划器对"点宽度超过上限"的处置同型）与 <see cref="NotSupportedException"/>
/// （类型不支持，与 <see cref="ByteDecoder"/> 对 Dtl/String 的处置同型）。
/// </para>
/// <para>
/// **刻意不校验的一条**（已知无害的宽容）：寄存器区**非 BOOL** 点的 <c>BitOffset != 0</c> 被忽略。
/// 它只在 BOOL 点上有意义（见 <see cref="ModbusAddress"/>），填了也只是冗余；为它报错会把
/// "配置工具多写了一个默认字段"变成采集失败。位区的 <c>BitOffset != 0</c> 则是另一回事——
/// 那里它表达的是"取第 n 位"这种**做不到的意图**，故响亮拒绝。
/// </para>
/// </remarks>
internal static class ModbusAddressValidator
{
    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法下界。0 是广播地址，不能用于读取。</summary>
    internal const int MinSlaveId = 1;

    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法上界。248~255 保留。</summary>
    internal const int MaxSlaveId = 247;

    /// <summary>
    /// 校验一个 Modbus 点是否可寻址。不满足即抛，**消息一律点名到点**（<c>PointCode</c> + <c>PointName</c>）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">从站号、地址、位偏移或寄存器区取值非法。</exception>
    /// <exception cref="NotSupportedException">类型与寄存器区的组合无法读取，或类型本期不支持。</exception>
    internal static void EnsureAddressable(PointConfig point)
    {
        var modbus = point.Modbus!;
        var where = $"采集点 {point.PointCode}（{point.PointName}）";

        if (modbus.SlaveId is < MinSlaveId or > MaxSlaveId)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.SlaveId,
                $"{where} 的从站号 {modbus.SlaveId} 超出 {MinSlaveId}~{MaxSlaveId}（规格 3.2 节 mb_slave）。"
                + "0 是广播地址、248~255 保留，它们会被编码成一个看起来合法的请求字节，"
                + "于是读到别的从站的数据而不会报错。请把从站号改为 1~247。");
        }

        switch (modbus.Area)
        {
            case ModbusRegisterArea.Coil:
            case ModbusRegisterArea.DiscreteInput:
                if (point.DataType != PointDataType.Bool)
                {
                    throw new NotSupportedException(
                        $"{where} 位于 {modbus.Area}（位区），每个地址只有一位，无法表达数据类型 {point.DataType}。"
                        + "位区只能配置 Bool 点；若该点确实是 16 位量，请把它改到保持寄存器/输入寄存器区。");
                }

                if (modbus.BitOffset != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        "points", modbus.BitOffset,
                        $"{where} 位于 {modbus.Area}（位区），按位寻址——一个线圈/离散输入就是一位，"
                        + $"BitOffset 必须为 0，实际为 {modbus.BitOffset}。静默忽略它会让'配置说取第 {modbus.BitOffset} 位、"
                        + "实际取了该地址本身'。");
                }

                break;

            case ModbusRegisterArea.HoldingRegister:
            case ModbusRegisterArea.InputRegister:
                if (point.DataType is PointDataType.Dtl or PointDataType.String)
                {
                    // 这道检查是**配置期**的，不是解码期的：放到解码期会带来三个后果
                    // （异常在 I/O 之后发生、异常不含点标识、异常中断整轮），
                    // 而配置错误是操作员必须修的东西，就该在最早、可归因的位置响亮暴露。
                    // ByteDecoder 侧对同样两个类型的检查**保留为第二道防线**（正常路径不可达）。
                    throw new NotSupportedException(
                        $"{where} 的数据类型 {point.DataType} 本期不支持（规格 8.1 节的已知限制）："
                        + "Dtl（S7 DATE_AND_TIME，8 字节 BCD）与 String（长度随地址与配置而定，PointConfig 里没有长度字段）"
                        + "在 Plan 1 都没有实现。此检查刻意放在配置期——若等到解码期才抛，"
                        + "异常会发生在一次白发的请求之后，且 ByteDecoder 拿不到 PointConfig、无法点名到点，操作员无法归因。"
                        + "请在配置里改用数值类型（BYTE/WORD/DWORD/SINT/USINT/INT/UINT/DINT/UDINT/REAL/LREAL），或等待后续版本。");
                }

                if (point.DataType == PointDataType.Bool && modbus.BitOffset is < 0 or > 15)
                {
                    throw new ArgumentOutOfRangeException(
                        "points", modbus.BitOffset,
                        $"{where} 是 {modbus.Area} 区的 Bool 点，寄存器内的位偏移必须在 0~15，实际为 {modbus.BitOffset}。"
                        + "越界的位偏移取不出任何位（该点会每周期写坏值）。");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    "points", modbus.Area,
                    $"{where} 的寄存器区 {modbus.Area}（{(int)modbus.Area}）不是已定义的值，无法确定读取用的功能码。");
        }

        if (modbus.RegisterAddress < 0)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.RegisterAddress,
                $"{where} 的地址 {modbus.RegisterAddress} 为负。Modbus 地址是 0 基的无符号 16 位量，"
                + "负值被转成 ushort 后会打到完全不相干的地址上。");
        }

        var width = ReadBlockPlanner.RegisterWidth(point);
        var lastRegister = modbus.RegisterAddress + width - 1;

        if (lastRegister > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.RegisterAddress,
                $"{where} 的地址 {modbus.RegisterAddress} 加上它占用的 {width} 个寄存器后，末寄存器地址 {lastRegister} "
                + $"越过 Modbus 地址上限 {ushort.MaxValue}，请求会回绕到块首附近，静默读到错的数据。");
        }
    }
}
