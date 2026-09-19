using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 读块合并。规格 5.2 节。
/// 不合并时 200 个点 = 200 次请求，1 秒周期下任何 PLC 都扛不住；
/// 合并后通常只需 5~15 次请求。
/// </summary>
public static class ReadBlockPlanner
{
    /// <summary>
    /// 为 Modbus 点集规划读块。
    /// </summary>
    /// <param name="points">
    /// 采集点，内部会按地址排序。
    /// <para>
    /// ⚠️ **没有 Modbus 地址的点（<see cref="PointConfig.Modbus"/> 为 null）会被静默排除**，
    /// 不报错、不计数、不返回任何提示。这是**有意行为**：本方法只处理 Modbus 点，
    /// 调用方（采集编排层）按连接协议分发点集，纯 S7 点不该走到这里。
    /// 但它意味着"误把 S7 点混进来"的后果是**计划里少了一些点而没有任何信号**——
    /// 采集器只会看到"这些点本轮没数据"，与"点被禁用"无法区分。
    /// 若你正在排障"某几个点永远没数据"，先检查它们的地址字段属于哪个协议。
    /// 该排除行为由 <c>没有_Modbus_地址的点被排除在计划之外</c> 测试锁定。
    /// </para>
    /// </param>
    /// <param name="mergeWindowRegisters">
    /// 合并窗口：相邻地址的空档不超过该值就合并（留空洞换取更少请求）。
    /// <para>
    /// ⚠️ **先分清两个容易混的量（本项目已因此写错过一次期望）**：
    /// <list type="bullet">
    ///   <item><b>地址差 D</b> = <c>下一个点的 RegisterAddress − 上一个点的 RegisterAddress</c>。
    ///         例如寄存器 100 与 116 → D = 16。</item>
    ///   <item><b>空档 G</b> = <c>下一个点的 RegisterAddress − blockEnd</c>，
    ///         其中 blockEnd = 上一个点的地址 + 它的寄存器宽度，即"上一个点之后第一个未被占用的寄存器"。
    ///         例如 100 处是一个 WORD（宽 1）→ blockEnd = 101，下一个点在 116 → G = 15。</item>
    /// </list>
    /// **判据用的是 G**（见 <c>pointStart - blockEnd &lt; mergeWindowRegisters</c>），
    /// 而"间隔"这个中文词在口头讨论里常被理解成 D。两者差一个"上一个点的宽度"。
    /// </para>
    /// <para>
    /// ⚠️ **实测边界（务必按实测理解，不要按参数名的字面含义理解）**：窗口 = 16 时，
    /// **空档 G ∈ 0..15 合并、G ≥ 16 拆分**。成因是判定用的是严格小于，
    /// 故可容忍的空档恰为 <c>0 .. 窗口-1</c>。即"窗口 16"的实际含义是"空档不超过 15"，
    /// 比参数名暗示的窄一格。三条用例（G=15 合并 / D=16（即 G=15）合并 / G≥16 拆分）钉死了它。
    /// **改这段代码前请先读这三条用例**——本边界极易被当成 off-by-one 的 bug 而"修"反。
    /// </para>
    /// </param>
    /// <param name="maxRegistersPerRequest">单次请求的寄存器数上限</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mergeWindowRegisters"/> 或 <paramref name="maxRegistersPerRequest"/> 小于 1；
    /// 或某个点的数据类型超出 <see cref="RegisterWidth"/> 的已知集合；
    /// 或某个点的寄存器宽度大于 <paramref name="maxRegistersPerRequest"/>（见下方 remarks）。
    /// </exception>
    /// <remarks>
    /// <para>
    /// **单个点的寄存器宽度必须不大于 <paramref name="maxRegistersPerRequest"/>，否则响亮抛异常。**
    /// 原因是物理性的：一个点的字节必须在**同一次响应**里被完整读回，无法拆到两次请求里再拼接
    /// （拼接需要跨请求维护半截缓冲，而规格 5.1 节要求一条连接由一个线程独占、按请求闭环）。
    /// 故这种配置下**必然**要发出一个超限请求，下游必然失败或静默截断——
    /// 让失败发生在更远处（连接层、甚至 PLC 返回异常码）会极难归因到"某个点的类型与上限不匹配"。
    /// 这是**配置期错误**，就该在规划期暴露：错误消息里点名是哪个点、宽度多少、上限多少。
    /// </para>
    /// <para>
    /// 注意该检查只作用于**有 Modbus 地址的点**（即真正参与规划的点）；
    /// 被 <see cref="PointConfig.Modbus"/> 过滤掉的点不参与校验。
    /// 上限恰好等于点宽度是**合法**的（块长等于上限，不变量仍成立）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ReadBlock> PlanForModbus(
        IEnumerable<PointConfig> points,
        int mergeWindowRegisters,
        int maxRegistersPerRequest)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (mergeWindowRegisters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeWindowRegisters), "合并窗口必须至少为 1");
        }

        if (maxRegistersPerRequest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRegistersPerRequest), "单次请求上限必须至少为 1");
        }

        // 校验与物化必须在**同一趟**里完成，原因不是性能而是正确性：
        // points 的静态类型是 IEnumerable，调用方可以传真正的一次性序列
        // （DbDataReader 支撑的枚举、保证只可走一遍的生成器）。先校验一趟、再 Where/OrderBy
        // 一趟的话，第二趟会拿到空序列 → ordered.Count == 0 → **返回空计划，一个点都不读且不报错**。
        // 实测（修复前）：一个"首次 GetEnumerator 产出、之后产出空"的序列
        // → GetEnumerator 调用 2 次、blocks=0、planned=0，无任何异常。
        // 下面这趟循环同时保持三条既有性质：
        //   ① 参数错误（窗口/上限 < 1）仍在枚举之前抛出；
        //   ② 违规点**立刻**抛出（fail fast，不读完点集）；
        //   ③ 无 Modbus 地址的点不参与上限校验（避免整组假失败）。
        var materialized = new List<PointConfig>();

        foreach (var point in points)
        {
            if (point.Modbus is not null && RegisterWidth(point) > maxRegistersPerRequest)
            {
                var width = RegisterWidth(point);
                throw new ArgumentOutOfRangeException(
                    nameof(maxRegistersPerRequest),
                    maxRegistersPerRequest,
                    $"单次请求上限 {maxRegistersPerRequest} 个寄存器，小于采集点 {point.PointCode}（{point.PointName}）"
                    + $" 的数据类型 {point.DataType} 所需的 {width} 个寄存器。"
                    + "单个点必须在同一次响应里完整读回、无法拆到两次请求，故此配置必然发出超限请求。"
                    + $"请把上限提高到至少 {width}，或把该点改为占用寄存器更少的数据类型。");
            }

            materialized.Add(point);
        }

        // 以下一律基于 materialized，绝不再触碰 points。
        var ordered = materialized
            .Where(p => p.Modbus is not null)
            .OrderBy(p => p.Modbus!.RegisterAddress)
            .ThenBy(p => p.PointId)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<ReadBlock>();
        }

        var blocks = new List<ReadBlock>();
        var currentPoints = new List<PointConfig> { ordered[0] };
        var blockStart = ordered[0].Modbus!.RegisterAddress;
        var blockEnd = blockStart + RegisterWidth(ordered[0]);

        for (var i = 1; i < ordered.Count; i++)
        {
            var point = ordered[i];
            var pointStart = point.Modbus!.RegisterAddress;
            var pointEnd = pointStart + RegisterWidth(point);
            var extendedLength = Math.Max(blockEnd, pointEnd) - blockStart;

            var canMerge = pointStart - blockEnd < mergeWindowRegisters
                           && extendedLength <= maxRegistersPerRequest;

            if (canMerge)
            {
                currentPoints.Add(point);
                blockEnd = Math.Max(blockEnd, pointEnd);
            }
            else
            {
                blocks.Add(new ReadBlock(blockStart, blockEnd - blockStart, currentPoints));

                currentPoints = [point];
                blockStart = pointStart;
                blockEnd = pointEnd;
            }
        }

        blocks.Add(new ReadBlock(blockStart, blockEnd - blockStart, currentPoints));

        return blocks;
    }

    /// <summary>
    /// 某个点占用多少个 Modbus 寄存器（16 位）。
    /// </summary>
    /// <param name="point">
    /// ⚠️ 注意异常里的 <c>ParamName</c>：这里以前写的是 <c>nameof(point)</c>，而 **<see cref="PlanForModbus"/>
    /// 的形参里没有 <c>point</c>**（只有 points / mergeWindowRegisters / maxRegistersPerRequest），
    /// 于是调用方按 ParamName 去定位参数时会扑空；同时旧消息也不含点的标识，
    /// 使得"某个点的 DataType 是未定义枚举值"这条路径报出来的异常**既指不到参数也指不到点**。
    /// 现在改为 "points"（见下方 switch 的说明：静态方法里取不到该形参，故用字面量）+ 消息里带上点的标识。
    /// </param>
    internal static int RegisterWidth(PointConfig point) => point.DataType switch
    {
        PointDataType.Bool => 1,
        PointDataType.Byte => 1,
        PointDataType.Word => 1,
        PointDataType.SInt => 1,
        PointDataType.USInt => 1,
        PointDataType.Int => 1,
        PointDataType.UInt => 1,
        PointDataType.DWord => 2,
        PointDataType.DInt => 2,
        PointDataType.UDInt => 2,
        PointDataType.Real => 2,
        PointDataType.LReal => 4,
        PointDataType.Dtl => 4,      // S7 DATE_AND_TIME 占 8 字节 = 4 个寄存器（解码层本期不支持，见 ByteDecoder）
        PointDataType.String => 1,   // 占位值：Modbus 字符串按字节流处理，真实宽度需地址表配置，PointConfig 无长度字段
        // ParamName 用字面量 "points" 而不是 nameof(points)：本方法是静态的、
        // 参数表里没有 points，nameof 在这里取不到该形参（已实测报 CS0103）。
        // 之所以仍报 "points"：调用方唯一能传点集进来的入口就是 PlanForModbus 的 points 形参，
        // 而 RegisterWidth 只在处理这些点时被调用（唯一例外是 ByteDecoder 侧的宽度概念，与参数无关）。
        _ => throw new ArgumentOutOfRangeException("points", point.DataType, UnknownDataTypeMessage(point)),
    };

    /// <summary>
    /// "未知数据类型"的错误消息。必须点名**是哪个点**的什么类型：
    /// 该分支只在某个点的 DataType 是未定义枚举值时触发，而调用方拿到异常后要定位到具体点，
    /// 只报类型名（例如 "9999"）在几百个点的配置里没有任何指向性。
    /// </summary>
    private static string UnknownDataTypeMessage(PointConfig point) =>
        $"采集点 {point.PointCode}（{point.PointName}）的数据类型 {point.DataType} 不是已定义的 {nameof(PointDataType)} 值，" +
        "无法确定它占用多少个 Modbus 寄存器。请检查该点的数据类型配置。";
}
