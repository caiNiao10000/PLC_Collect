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
    /// 合并窗口：相邻地址的间隔不超过该值就合并（留空洞换取更少请求）。
    /// <para>
    /// ⚠️ **实测边界（务必按实测理解，不要按参数名的字面含义理解）**：窗口 = 16 时，
    /// **间隔 0..15 合并、间隔 16 及以上拆分**。成因是 <c>pointStart - blockEnd &lt; mergeWindowRegisters</c>
    /// 用的是严格小于，而 blockEnd 指向"上一个点之后第一个未被占用的寄存器"，
    /// 故可容忍的间隔恰为 <c>0 .. 窗口-1</c>。即"窗口 16"的实际含义是"间隔不超过 15"，
    /// 比参数名暗示的窄一格。三条用例（间隔 15 合并 / 间隔 16 合并 / 间隔 17 拆分）钉死了它。
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

        // 先把"单个点就超限"这一配置期错误挡掉，再进入排队与分块。
        // 校验必须与规划用同一套过滤（只看有 Modbus 地址的点），否则会出现
        // "因为一个根本不会被规划的点而整组失败"的假失败。
        EnsureNoPointExceedsRegisterLimit(points, maxRegistersPerRequest);

        var ordered = points
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
    /// 拦住"单个点的寄存器宽度就超过单请求上限"的配置。
    /// </summary>
    /// <remarks>
    /// **为什么是 <see cref="ArgumentOutOfRangeException"/> 而不是 <see cref="InvalidOperationException"/>：**
    /// <list type="bullet">
    ///   <item>本例里**唯一出问题的参数值就是 <paramref name="maxRegistersPerRequest"/>**：
    ///         那些点本身完全合法（一个 LREAL 点在任何"上限 ≥ 4"的配置下都合法），
    ///         换一个更大的上限就立刻可用。故把参数名与实参带上，排障时一眼看到该改哪个数。
    ///         这与"对象当前状态不允许该操作"（InvalidOperationException）的语义不同——
    ///         这里没有状态，只有参数与输入不匹配。</item>
    ///   <item>本方法已有的参数校验（窗口/上限小于 1）都用 <see cref="ArgumentOutOfRangeException"/>，
    ///         保持调用方 catch 同一种异常即可覆盖全部参数问题。</item>
    ///   <item>把出问题的点写进异常消息（而不是只报 <c>maxRegistersPerRequest=2</c>），
    ///         因为"上限该设多大"取决于配置里最宽的那个点，而调用方从参数值本身看不出来。</item>
    /// </list>
    /// </remarks>
    private static void EnsureNoPointExceedsRegisterLimit(
        IEnumerable<PointConfig> points,
        int maxRegistersPerRequest)
    {
        foreach (var point in points)
        {
            // 不参与规划的点（Modbus 地址为 null）不校验，与 PlanForModbus 的过滤保持一致；
            // 否则会出现"因为一个根本不会被规划的点而整组失败"的假失败。
            // 用单表达式条件而非 if+continue：把这段判定写成独立语句时，
            // 任何"跳过校验"的变异都会立刻触发 CS0162（无法检测的代码）——那只能证明编译器在工作，
            // 拿不到"断言失败"的合格证据。写成条件表达式后，同一变异产生可观测的行为差异。
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
        }
    }

    /// <summary>某个点占用多少个 Modbus 寄存器（16 位）。</summary>
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
        PointDataType.Dtl => 4,
        PointDataType.String => 1,   // Modbus 字符串按字节流处理，具体宽度由地址与长度决定
        _ => throw new ArgumentOutOfRangeException(nameof(point), point.DataType, "未知数据类型"),
    };
}
