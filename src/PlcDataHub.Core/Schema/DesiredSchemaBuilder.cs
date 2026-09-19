using PlcDataHub.Core.Model;
using PlcDataHub.Core.Naming;

namespace PlcDataHub.Core.Schema;

/// <summary>
/// 配置图 → 期望结构。规格 3.4 节。
/// 规则：停用的连接 / 组 / 点一律不进期望结构（不生成表、不生成列）。
/// </summary>
public static class DesiredSchemaBuilder
{
    public static DesiredSchema Build(
        IEnumerable<DeviceConnection> connections,
        IEnumerable<PollGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);

        var enabledConnIds = connections
            .Where(c => c.Enabled)
            .Select(c => c.ConnId)
            .ToHashSet();

        var tables = new List<DesiredTable>();

        foreach (var group in groups.Where(g => g.Enabled && enabledConnIds.Contains(g.ConnId)))
        {
            tables.Add(BuildTable(group));
        }

        return new DesiredSchema(tables);
    }

    private static DesiredTable BuildTable(PollGroup group)
    {
        var enabledPoints = group.Points.Where(p => p.Enabled).ToList();

        var columns = new List<DesiredColumn>
        {
            // 固定三列，顺序固定：ts / q / src_ts
            new(DesiredSchema.TimestampColumn, "timestamp", IsNullable: false),
            new(DesiredSchema.QualityColumn, "smallint", IsNullable: false),
            new(DesiredSchema.SourceTimestampColumn, "timestamp", IsNullable: true),
        };

        // 点为 NULL 是常态（坏点写 NULL），所以数据列一律允许 NULL
        foreach (var (point, columnName) in AssignColumns(enabledPoints))
        {
            columns.Add(new DesiredColumn(
                columnName,
                SqlTypeMapper.ToPostgresType(point.DataType),
                IsNullable: true));
        }

        return new DesiredTable(group.TableName, columns);
    }

    /// <summary>
    /// 决定每个点最终的列名：优先用手工指定的 column_name；
    /// 为空的名由 ColumnNameGenerator 生成；整组重名统一避让由生成器负责。
    /// </summary>
    private static IEnumerable<(PointConfig Point, string ColumnName)> AssignColumns(
        IReadOnlyList<PointConfig> points)
    {
        // 先用手工列名占位，再为剩余的自动命名，避免自动名抢占手工名
        var manual = points
            .Select((p, index) => (Point: p, Index: index))
            .Where(x => !string.IsNullOrWhiteSpace(x.Point.ColumnName))
            .ToList();

        var reserved = manual
            .Select(x => x.Point.ColumnName)
            .ToHashSet(StringComparer.Ordinal);

        var assigned = new string?[points.Count];

        foreach (var (point, index) in manual)
        {
            assigned[index] = point.ColumnName;
        }

        var autoNeeded = points
            .Select((p, index) => (Point: p, Index: index))
            .Where(x => string.IsNullOrWhiteSpace(x.Point.ColumnName))
            .ToList();

        if (autoNeeded.Count > 0)
        {
            // 注：生成器只保证本批内部不重复，不知道手工列名占用了哪些名字，
            // 因此下面还要再和 reserved 对一次；生成器不会抛出，故此调用可安全地留在迭代器内。
            var generated = ColumnNameGenerator.AssignUniqueColumns(
                autoNeeded.Select(x => x.Point.PointName));

            var cursor = 0;
            foreach (var (_, index) in autoNeeded)
            {
                // cursor++ 先自增，故下面用 generated[cursor - 1] 回指"本点刚刚取到的那一个"，
                // 也就是本点自动名的**基名**。这不是笔误：整段循环里对 generated 的读取
                // 只有这一次自增，cursor - 1 恒等于当前项下标。
                var candidate = generated[cursor++];
                var suffix = 2;

                // 兜底避让：自动名撞上手工列名时追加序号。
                // 用 generated[cursor - 1] 而不是重新 Normalize(PointName) 作基名，
                // 是为了不重复规范化、也避免两处规范化结果漂移。
                // 已知代价（见 task-4-report.md）：若手工列名恰好占用 "X_2"，
                // 而某点的自动名也是 "X_2"，则避让结果是 "X_2_2" 而不是更自然的 "X_3"。
                // 这只是命名风格，不构成正确性缺陷 —— 列名仍唯一且合法。
                // 唯一性由"先查 reserved 再加入 reserved"保证，无需依赖生成器行为。
                while (reserved.Contains(candidate))
                {
                    candidate = $"{generated[cursor - 1]}_{suffix++}";
                }

                reserved.Add(candidate);
                assigned[index] = candidate;
            }
        }

        for (var i = 0; i < points.Count; i++)
        {
            yield return (points[i], assigned[i]!);
        }
    }
}
