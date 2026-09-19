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
    /// 或某个点的数据类型超出 <see cref="RegisterWidth"/> 的已知集合。
    /// </exception>
    /// <remarks>
    /// **本方法不校验 <paramref name="maxRegistersPerRequest"/> 与单个点宽度的关系**：
    /// 若某个点自身占用的寄存器数就超过上限（例如上限 2 却有一个 LREAL 点占 4 个寄存器），
    /// 该块的长度必然超过上限——这是**约束不足**，不是实现缺陷：一个点无法被拆到两次请求里
    /// 解码。当前规格没有给出这种配置下的期望行为，故既不静默截断也不抛异常，见 task-6-report.md §7。
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
