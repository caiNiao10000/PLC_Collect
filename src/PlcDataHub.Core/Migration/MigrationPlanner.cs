using System.Globalization;
using System.Text;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Naming;

namespace PlcDataHub.Core.Migration;

/// <summary>
/// 期望结构 vs 现有结构 → 迁移差异计划。规格 4.1 / 4.2 节。
/// 纯计算，不碰数据库。所有 SQL 只生成不执行。
/// </summary>
public static class MigrationPlanner
{
    /// <summary>数据表所在的 schema。规格 3.2 节。</summary>
    public const string DataSchema = "d";

    /// <summary>时间列索引名后缀。</summary>
    private const string TimestampIndexSuffix = "_ts_idx";

    /// <summary>软删除列名的固定前缀。</summary>
    private const string SoftDeletePrefix = "deleted_";

    /// <summary>软删除列名里保留的原列名字符数上限，给前缀与日期留出字节预算。</summary>
    private const int SoftDeleteKeepChars = 40;

    /// <summary>用默认（安全优先）选项生成迁移计划。</summary>
    public static MigrationPlan Plan(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing) =>
        Plan(desired, existing, new MigrationOptions());

    /// <summary>生成迁移计划。</summary>
    /// <exception cref="MigrationConflictException">存在列类型冲突时抛出。</exception>
    /// <exception cref="InvalidOperationException">标识符非法（含非 ASCII、内嵌引号、控制字符）时抛出。</exception>
    public static MigrationPlan Plan(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing,
        MigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);

        // 所有标识符先过一遍校验：非法标识符会直接拼进 DDL（SQL 注入面），
        // 必须在生成任何语句之前响亮失败，而不是产出半份计划。
        GuardIdentifiers(desired, existing);

        // PostgreSQL 未加引号的标识符大小写不敏感（会被折叠成小写），
        // 故"库里的 Wen_Du" 与"期望的 wen_du"是**同一列**，比对必须忽略大小写；
        // 用 Ordinal 会误判为缺列并生成 ADD COLUMN，而该列实际已存在 → 迁移失败。
        var existingByName = existing.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
        var desiredNames = desired.Tables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var steps = new List<MigrationStep>();

        foreach (var table in desired.Tables)
        {
            if (existingByName.TryGetValue(table.TableName, out var existingTable))
            {
                steps.AddRange(PlanExistingTable(table, existingTable, options));
            }
            else
            {
                steps.AddRange(PlanNewTable(table));
            }
        }

        // 库里有、配置里没有的表
        foreach (var orphan in existing.Where(t => !desiredNames.Contains(t.TableName)))
        {
            if (options.DropRemovedTables)
            {
                steps.Add(new MigrationStep(
                    MigrationStepKind.DropTable,
                    orphan.TableName,
                    $"drop table {Qualified(orphan.TableName)}",
                    IsDestructive: true,
                    $"删除表 {Qualified(orphan.TableName)}，将丢弃 {orphan.RowCount.ToString("N0", CultureInfo.InvariantCulture)} 行数据"));
            }

            // DropRemovedTables=false 时什么都不做：规格 4.2 节，采集组删除默认只停采集、不删表
        }

        return new MigrationPlan(steps);
    }

    private static IEnumerable<MigrationStep> PlanNewTable(DesiredTable table)
    {
        var columnDefs = table.Columns.Select(ColumnDefinitionSql);

        yield return new MigrationStep(
            MigrationStepKind.CreateTable,
            table.TableName,
            $"create table if not exists {Qualified(table.TableName)} (\n  {string.Join(",\n  ", columnDefs)}\n)",
            IsDestructive: false,
            $"新建表 {Qualified(table.TableName)}，共 {table.Columns.Count} 列");

        yield return new MigrationStep(
            MigrationStepKind.CreateIndex,
            table.TableName,
            $"create index if not exists {IndexName(table.TableName)} on {Qualified(table.TableName)} ({DesiredSchema.TimestampColumn})",
            IsDestructive: false,
            $"为 {Qualified(table.TableName)} 建时间索引");
    }

    private static IEnumerable<MigrationStep> PlanExistingTable(
        DesiredTable table,
        ExistingTable existing,
        MigrationOptions options)
    {
        // 大小写不敏感：PG 未加引号的标识符折叠为小写，Wen_Du 与 wen_du 是同一列。
        // 用循环而不是 ToDictionary —— 同一张表里同时存在 "Wen_Du" 与 "wen_du" 这种
        // 加引号建出来的库，ToDictionary 会抛"已添加相同键的项"，
        // 那是与本方法语义无关的异常；取先出现的一列即可（同名重复列本就不该存在）。
        var existingColumns = new Dictionary<string, ExistingColumn>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in existing.Columns)
        {
            existingColumns.TryAdd(column.Name, column);
        }

        foreach (var column in table.Columns)
        {
            if (!existingColumns.TryGetValue(column.Name, out var actual))
            {
                yield return new MigrationStep(
                    MigrationStepKind.AddColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} add column if not exists {ColumnDefinitionSql(column)}",
                    IsDestructive: false,
                    $"新增列 {column.Name}（历史行该列为 NULL）");
                continue;
            }

            if (!TypesEquivalent(column.PostgresType, actual.PostgresType))
            {
                // 规格 4.2 节：类型变更绝不自动执行
                throw new MigrationConflictException(
                    $"表 {Qualified(table.TableName)} 的列 {column.Name} 类型冲突：" +
                    $"数据库现有类型为 {actual.PostgresType}，配置要求的类型为 {column.PostgresType}。" +
                    "请选择「新建一列」或「确认丢弃该列数据后重建」，软件不会自动修改列类型。");
            }
        }

        // 配置里没有、但库里存在、且不是软删除遗留的列
        var desiredNames = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var actual in existing.Columns.Where(c => !desiredNames.Contains(c.Name)))
        {
            // 判定用的是"以前缀开头"而不是"等于今天生成的某个具体名字"：
            // 上一轮软删除产生的 deleted_<名>_<某天日期> 在**任何后续日期**都必须被跳过，
            // 否则第二次运行会把它当成一个"配置里没有的新列"再改名一次，迁移就不再幂等。
            if (actual.Name.StartsWith(SoftDeletePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.SoftDeleteRemovedColumns)
            {
                var newName = SoftDeletedName(actual.Name);
                yield return new MigrationStep(
                    MigrationStepKind.SoftDeleteColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} rename column \"{actual.Name}\" to \"{newName}\"",
                    IsDestructive: false,
                    $"列 {actual.Name} 已不在配置中，改名为 {newName} 保留数据");
            }
            else
            {
                yield return new MigrationStep(
                    MigrationStepKind.DropColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} drop column \"{actual.Name}\"",
                    IsDestructive: true,
                    $"删除列 {actual.Name}，将丢弃该列全部历史数据（表共 {existing.RowCount.ToString("N0", CultureInfo.InvariantCulture)} 行）");
            }
        }
    }

    /// <summary>
    /// 一列在建表 / 加列语句里的定义片段。
    /// <para>
    /// 规格 3.4 节要求 <c>q smallint not null default 0</c>，但 <see cref="DesiredColumn"/>
    /// 只表达 <c>(Name, PostgresType, IsNullable)</c>、**无法表达默认值**，
    /// 故这里为质量列硬编码 <c>default 0</c>。缺了它，历史行与"没写 q 的插入"会落成 NULL，
    /// 与规格"q not null"矛盾。
    /// </para>
    /// <para>
    /// 只认小写 <c>q</c>：<see cref="DesiredSchemaBuilder"/> 产出的固定列恰为小写 <c>q</c>，
    /// 且它的大小写不敏感去重守卫会拦住任何撞固定列的手工名 / 显示名，
    /// 故"名为 q 的列"必然是质量列。用户写的 <c>"Q"</c> 在加引号的 DDL 里是**另一个列**，
    /// 不享受这个默认值。
    /// </para>
    /// </summary>
    private static string ColumnDefinitionSql(DesiredColumn column)
    {
        var nullability = column.IsNullable ? "null" : "not null";
        var defaultClause = column.Name == DesiredSchema.QualityColumn ? " default 0" : string.Empty;
        return $"\"{column.Name}\" {column.PostgresType} {nullability}{defaultClause}";
    }

    /// <summary>软删除列名：deleted_{截断的原列名}_{yyyymmdd}，并保证不超 63 字节。</summary>
    private static string SoftDeletedName(string originalName)
    {
        var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var suffix = $"_{date}";
        var budget = ColumnNameGenerator.MaxIdentifierBytes
                     - Encoding.UTF8.GetByteCount(SoftDeletePrefix)
                     - Encoding.UTF8.GetByteCount(suffix);

        var keep = originalName.Length > SoftDeleteKeepChars
            ? originalName[..SoftDeleteKeepChars]
            : originalName;

        while (Encoding.UTF8.GetByteCount(keep) > budget && keep.Length > 0)
        {
            keep = keep[..^1];
        }

        return SoftDeletePrefix + keep + suffix;
    }

    private static string IndexName(string tableName) => tableName + TimestampIndexSuffix;

    private static string Qualified(string tableName) => $"{DataSchema}.{tableName}";

    /// <summary>
    /// 标识符校验：本任务承接的两项（Task 4 的 <c>GuardColumns</c> 只拦"重复"与"超 63 字节"）。
    /// <para>
    /// ① <b>非 ASCII → 拒绝</b>。标识符会加双引号进 DDL，非 ASCII 会让 psql / Excel 导出乱码，
    /// 且规格 3.5 节要求"列名统一使用 ASCII"。自动名已由 <c>ColumnNameGenerator</c> 净化，
    /// 但**手工列名被原样赋值、从不经过规范化**，故必须在这里拦。
    /// </para>
    /// <para>
    /// ② <b>内嵌引号 / 反斜杠 / 控制字符 → 拒绝</b>（SQL 注入面，README-VALIDATION.md 专门警告过）。
    /// 本项目选择"拒绝"而不是"转义"：转义只能挡住引号，挡不住 psql 元命令（如列名里带 <c>\n</c>），
    /// 而且会把一个可疑输入静默改写成另一个标识符 —— 与"绝不静默"的取向相反。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不在这里重复"长度"与"重复"校验</b>：Task 4 的 <c>GuardColumns</c> 已对期望结构的
    /// 全部列（手工名与自动名一视同仁）拦下超 63 字节与重复，两处实现会形成漂移。
    /// 因此长度只用于 <see cref="SoftDeletedName"/> 的生成预算（生成侧，不是校验侧）——
    /// 这也意味着**数据库现存的超长列名不会被本方法拒绝**，只会被生成侧截断；
    /// 该取舍与 Task 4 报告 §16.1 的"不要重复实现长度校验"一致，如实记录以便后续复核。
    /// </para>
    /// </summary>
    /// <param name="desired">期望结构（长度与重复校验已由 Task 4 完成）</param>
    /// <param name="existing">数据库现存结构（第三方输入；只查非 ASCII 与需转义字符，不查长度）</param>
    /// <exception cref="InvalidOperationException">标识符为空、含非 ASCII、引号、反斜杠或控制字符。</exception>
    private static void GuardIdentifiers(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing)
    {
        foreach (var table in desired.Tables)
        {
            GuardIdentifier(table.TableName, table.TableName, "表名");

            foreach (var column in table.Columns)
            {
                GuardIdentifier(table.TableName, column.Name, "列名");
            }
        }

        foreach (var table in existing)
        {
            GuardIdentifier(table.TableName, table.TableName, "数据库现有表名");

            foreach (var column in table.Columns)
            {
                GuardIdentifier(table.TableName, column.Name, "数据库现有列名");
            }
        }
    }

    private static void GuardIdentifier(string tableName, string identifier, string role)
    {
        if (identifier.Length == 0)
        {
            throw new InvalidOperationException(
                $"表 {tableName} 的{role}为空。空标识符会让 DDL 里出现一对空引号而报语法错误，" +
                "请修正该列名（列的 ColumnName 留空表示「由显示名自动生成」，不应产出空名）。");
        }

        foreach (var ch in identifier)
        {
            if (ch is '"' or '\\')
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的{role} {identifier} 含需转义字符（引号或反斜杠），" +
                    "直接拼接进 DDL 会构成 SQL 注入。请修改该列名，不要使用引号或反斜杠。");
            }

            if (char.IsControl(ch))
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的{role} {identifier} 含控制字符，直接拼接进 DDL 会构成 SQL 注入。" +
                    "请修改该列名，只使用可见字符。");
            }

            if (ch > '\u007f')
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的{role} {identifier} 含非 ASCII 字符，而标识符一律要求 ASCII" +
                    "（规格 3.5 节）。中文请放到显示名里由生成规则转拼音，" +
                    "手工列名只允许 ASCII 字母、数字与下划线。");
            }
        }
    }

    /// <summary>
    /// 类型等价判定。期望结构里写的是标准名（integer / timestamp），
    /// 而 information_schema 返回的是 int4 / timestamp without time zone 这类别名，
    /// 必须视为等价，否则每次迁移都会误判为冲突。
    /// </summary>
    private static bool TypesEquivalent(string desired, string actual)
    {
        static string Canonical(string type) => type.Trim().ToLowerInvariant() switch
        {
            "int" or "int4" or "integer" => "integer",
            "int2" or "smallint" => "smallint",
            "int8" or "bigint" => "bigint",
            "bool" or "boolean" => "boolean",
            "float4" or "real" => "real",
            "float8" or "double precision" => "double precision",
            "timestamp" or "timestamp without time zone" => "timestamp",
            "timestamptz" or "timestamp with time zone" => "timestamptz",
            "varchar" or "character varying" => "text",
            var other => other,
        };

        return Canonical(desired) == Canonical(actual);
    }
}
