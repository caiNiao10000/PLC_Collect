using System.Text;
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

        GuardColumns(group.TableName, columns);

        return new DesiredTable(group.TableName, columns);
    }

    /// <summary>
    /// 列名的最后一道防线：**重复**（含仅大小写不同）与**超过 63 字节**。
    /// 判据来自规格 3.2 节"同一组内 column_name 唯一（对应表中列唯一）"与 3.5 节的 PG 标识符上限。
    /// 放在 <see cref="BuildTable"/> 末尾而非只查数据列，顺带覆盖固定列（手工写 <c>TS</c> 会撞固定列 <c>ts</c>）。
    /// <para>
    /// 【重复】PostgreSQL 未加引号的标识符大小写不敏感、会被折叠成小写，故 <c>Wen_Du</c> 与 <c>wen_du</c>
    /// 在库里是**同一列**：期望结构里两者并存，生成的 CREATE TABLE 必报 duplicate column name。
    /// 覆盖两个来源：手工列名之间、以及手工列名与自动生成列名之间。
    /// （自动名恒为纯小写 —— <c>ColumnNameGenerator.Normalize</c> 对 ASCII 大写字母做
    /// <c>ToLowerInvariant</c>，且结果由既有测试锁定为 <c>^[a-z0-9_]+$</c> ——
    /// 故自动名之间不可能仅大小写不同。）
    /// </para>
    /// <para>
    /// 【长度】PG 标识符上限 63 字节，超出会被**静默截断**；截断后可能与同表另一列同名。
    /// 触发路径：<see cref="AssignColumns"/> 的兜底避让是裸拼接 <c>$"{基名}_{序号}"</c>，
    /// 不经生成器的字节预算，故基名恰为 63 字节时会拼出 65 字节
    /// （对比 <c>ColumnNameGenerator.AssignUniqueColumns</c> 内部明确按"给序号留位置"重新截断）。
    /// </para>
    /// <para>
    /// 为什么是"抛异常"而不是静默小写化 / 静默去重 / 静默截断：静默改写用户输入会让
    /// "配置写 <c>Wen_Du</c>、库里却是 <c>wen_du</c>"、或"配置写 63 字节、库里被截成另一个名字"，
    /// 之后按配置查库会找不到列，是更难查的故障；静默丢弃更糟。
    /// 在配置期响亮失败、并点名冲突的两个列名，用户才知道该改哪个。与本项目"绝不静默"的取向一致。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">列名重复（含仅大小写不同）或超过 63 字节。</exception>
    private static void GuardColumns(string tableName, IReadOnlyList<DesiredColumn> columns)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            // PostgreSQL 标识符上限是 63 字节，超出会被**静默截断**；截断后可能与本表另一列同名，
            // 导致建表报 duplicate column name。静默截断只有本模块看得见，故必须自己拦住。
            // 触发路径：兜底避让是裸拼接（$"{基名}_{序号}"），不经生成器的字节预算，
            // 故基名恰为 63 字节时会拼出 65 字节。
            var byteCount = Encoding.UTF8.GetByteCount(column.Name);

            if (byteCount > ColumnNameGenerator.MaxIdentifierBytes)
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的列名 {column.Name} 长度为 {byteCount} 字节，" +
                    $"超过 PostgreSQL 标识符上限 {ColumnNameGenerator.MaxIdentifierBytes} 字节。" +
                    "PostgreSQL 会静默截断，截断后可能与本表另一列同名并导致建表失败。" +
                    "请缩短该采集点的显示名或手工列名。");
            }

            if (seen.TryGetValue(column.Name, out var existing))
            {
                throw new InvalidOperationException(
                    existing == column.Name
                        ? $"表 {tableName} 中存在重复列名 {column.Name}，请修改其中一个采集点的手工列名。"
                        : $"表 {tableName} 中存在仅大小写不同的重复列名：{existing} 与 {column.Name}。" +
                          "PostgreSQL 未加引号的标识符大小写不敏感，两者在库中是同一列，建表必失败。" +
                          "请修改其中一个采集点的手工列名。");
            }

            seen.Add(column.Name, column.Name);
        }
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

                // 兜底避让：自动名撞上手工列名（或前一个点的避让结果）时追加序号。
                // 用 generated[cursor - 1] 而不是重新 Normalize(PointName) 作基名，
                // 是为了不重复规范化、也避免两处规范化结果漂移。
                //
                // ⚠️ suffix++ 是**终止性承重件，禁止删除**：
                // 候选名可能被"手工名"连环占用（例：手工列名占了 wen_du 与 wen_du_2，
                // 而某自动点的基名正是 wen_du）——此时只有序号递增到 3 才能跳出循环；
                // 若序号不递增（如写成常量 _2），候选恒被占用 ⇒ 死循环。
                // 该终止性由 DesiredSchemaBuilderTests.候选名被手工名连环占用时仍能终止且列名唯一 锁定。
                //
                // 命名风格说明（非缺陷）：避让结果是"基名_序号"，故手工名恰好占用 "X_2"、
                // 某点自动名也是 "X_2" 时会得到 "X_2_2" 而非更自然的 "X_3"。列名仍唯一且合法。
                // 唯一性由"先查 reserved 再加入 reserved"保证，不依赖生成器行为。
                //
                // ⚠️ 本循环是**裸拼接**，不经 ColumnNameGenerator 的字节预算，故基名恰为 63 字节时
                // 会拼出 65 字节列名 —— 由 GuardColumns 的长度检查响亮拦下（不在此处静默截断，
                // 因为静默截断会改写用户输入、使配置与库内列名不一致）。
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
