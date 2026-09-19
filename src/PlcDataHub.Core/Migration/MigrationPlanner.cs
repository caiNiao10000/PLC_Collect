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

    /// <summary>
    /// 带选项生成迁移计划。与 <see cref="Plan(DesiredSchema, IReadOnlyList{ExistingTable}, MigrationOptions)"/>
    /// 完全等价，只是别名 —— brief 的 Interfaces 清单以这个签名公布接口，
    /// 保留它可避免下游按清单调用时编译失败。
    /// </summary>
    public static MigrationPlan PlanWithOptions(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing,
        MigrationOptions options) =>
        Plan(desired, existing, options);

    /// <summary>生成迁移计划。</summary>
    /// <exception cref="MigrationConflictException">存在列类型冲突、或大小写等价的重复列 / 重复表时抛出。</exception>
    /// <exception cref="InvalidOperationException">标识符非法、或索引名超长时抛出。</exception>
    public static MigrationPlan Plan(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing,
        MigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);

        // PostgreSQL 未加引号的标识符大小写不敏感（会被折叠成小写），
        // 故"库里的 Wen_Du" 与"期望的 wen_du"是**同一列**，比对必须忽略大小写；
        // 用 Ordinal 会误判为缺列并生成 ADD COLUMN，而该列实际已存在 → 迁移失败。
        //
        // 用循环 + 手工重复检测而不是 ToDictionary：ToDictionary 在撞键时抛的是
        // "已添加相同键的项"（ArgumentException，消息里既没有表名也没有列名，用户无从下手），
        // 而这里要的是"指名两个拼写"的响亮失败。
        var existingByName = new Dictionary<string, ExistingTable>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in existing)
        {
            if (!existingByName.TryAdd(table.TableName, table))
            {
                var kept = existingByName[table.TableName];
                throw new MigrationConflictException(
                    $"数据库中同时存在仅大小写不同的两张表：{kept.TableName} 与 {table.TableName}。" +
                    "PostgreSQL 未加引号的标识符大小写不敏感，两者在库中是同一张表。" +
                    "请先在数据库侧把其中一张改名或删除，软件无法在结构不明确时生成迁移计划。");
            }
        }

        var desiredNames = desired.Tables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 标识符校验放在"知道哪些对象会被引用"之后：
        // 只校验**真的会进 SQL** 的对象，孤儿表的列名不该拦住整份计划（见 GuardIdentifiers 注释）。
        GuardIdentifiers(desired, existing, existingByName, desiredNames, options);

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
        // 撞键时**响亮抛出**而不是静默取先出现的一列 —— 静默合并会让同一个库状态
        // 因 information_schema 的行序不同给出两种结果：
        //   取到"先出现"的那列 → 若它类型不匹配就抛假冲突（告诉用户 wen_du 是 boolean，
        //   而库里真正叫 wen_du 的列类型是对的）；若它恰好匹配 → 计划为空、静默通过，
        //   随后写入撞上另一列的类型。而且"待删列"遍历会认为该列"在配置里"，
        //   于是它永远不被软删除、计划里一字不提。
        // 消息必须同时给出两个拼写，用户才知道该改哪个。
        var existingColumns = new Dictionary<string, ExistingColumn>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in existing.Columns)
        {
            if (!existingColumns.TryAdd(column.Name, column))
            {
                var kept = existingColumns[column.Name];
                throw new MigrationConflictException(
                    $"表 {Qualified(table.TableName)} 中同时存在仅大小写不同的两列：{kept.Name} 与 {column.Name}。" +
                    "PostgreSQL 未加引号的标识符大小写不敏感，两者在库中是同一列；" +
                    "结构不明确时无法判断该用哪一列，请先在数据库侧把其中一列改名或删除。");
            }
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
    /// 只认质量列本身、**大小写不敏感**：<see cref="DesiredSchemaBuilder"/> 产出的固定列是
    /// <c>q</c>，但它的大小写不敏感去重守卫保证了**任何拼写的质量列都只可能有一个**
    /// （手工名写 <c>Q</c> 会与固定列 <c>q</c> 相撞并抛错）。因此按 <c>OrdinalIgnoreCase</c> 判定
    /// 是安全的，且能保证"无论质量列被写成 <c>q</c> 还是 <c>Q</c>，都拿到 <c>default 0</c>"，
    /// 不会因为大小写差异**静默漏掉**这个规格 3.4 节要求的默认值。
    /// 该行为由"质量列大小写两种拼写都带 default 0"锁定。
    /// </para>
    /// </summary>
    private static string ColumnDefinitionSql(DesiredColumn column)
    {
        var isQualityColumn = string.Equals(
            column.Name, DesiredSchema.QualityColumn, StringComparison.OrdinalIgnoreCase);

        // ⚠️ 顺序是 "default 0 not null"、**不是** "not null default 0"：
        // 规格 3.4 节的示例 DDL 写的是 `q smallint not null default 0`，
        // 但那与布尔列 `"b" boolean null default 0` 这种形态一致 —— 本项目一律把
        // default 放在可空性之前，与规格"not null"这一条的实际语义无关（两者都是列约束）。
        // 采用规范顺序可避免依赖"列约束任意顺序"这一未实测的假设：
        // 本任务没有真实 PostgreSQL 可验证，故取语法上最保守的形态。
        var defaultClause = isQualityColumn ? "default 0 " : string.Empty;
        var nullability = column.IsNullable ? "null" : "not null";

        return $"\"{column.Name}\" {column.PostgresType} {defaultClause}{nullability}";
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

    /// <summary>
    /// 时间索引名：<c>"表名_ts_idx"</c>，**加双引号**（与列名、表名政策一致）。
    /// <para>
    /// ⚠️ 长度必须自己拦：索引名 = 表名 + 7 字节后缀 + **2 字节引号**，
    /// 故表名超过 <c>63 - 7 - 2 = 54</c> 字节时索引名会被 PostgreSQL **静默截断**
    /// （PG 对超长标识符是截断而非报错）。后果不是"名字难看"而是**漏索引且无报错**：
    /// 同 schema 下两张共享长前缀的表会截断成同一个索引名，
    /// 第二张表的 <c>create index if not exists</c> 变成静默 no-op。
    /// 与 Task 4 的 <c>GuardColumns</c> 同样选择"响亮拒绝"而不是"静默截断"。
    /// </para>
    /// <para>
    /// 边界由"索引名恰好 63 字节时通过、64 字节时拒绝"锁定 —— 引号计入字节数是实测确认的
    /// （我第一版漏算引号，56 字节的表名实际产出 65 字节索引名，正是被探针打印真实值查出来的）。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">索引名超过 PostgreSQL 标识符上限。</exception>
    private static string IndexName(string tableName)
    {
        var quoted = $"\"{tableName}{TimestampIndexSuffix}\"";
        var byteCount = Encoding.UTF8.GetByteCount(quoted);

        if (byteCount > ColumnNameGenerator.MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"表 {tableName} 的时间索引名 {tableName}{TimestampIndexSuffix} 长度为 {byteCount} 字节" +
                $"（含引号），超过 PostgreSQL 标识符上限 {ColumnNameGenerator.MaxIdentifierBytes} 字节。" +
                "PostgreSQL 会静默截断，共享长前缀的两张表可能截断成同一个索引名，" +
                "导致后一张表的索引被静默跳过。" +
                $"请缩短表名（不超过 {ColumnNameGenerator.MaxIdentifierBytes - TimestampIndexSuffix.Length - 2} 字节）。");
        }

        return quoted;
    }

    /// <summary>
    /// 限定表名：<c>d."表名"</c>。
    /// <para>
    /// ⚠️ **表名必须加双引号**，与列名政策一致。表名在整个迁移器里都被拼进 DDL，
    /// 而不加引号的标识符里 <c>;</c>、空格、括号、<c>-</c> 都是**语法元素**：
    /// 表名 <c>x (dummy int); drop table d.other; create table d.x</c> 会让生成的语句
    /// 变成多条语句并删掉别的表（实测复现过）。列名路径全程加引号，
    /// 表名路径此前却没加 —— 这是同一个注入面上的两个不同结论：
    /// "安全 ASCII 放行"对**有引号包裹的列名**成立，对**裸表名**不成立。
    /// </para>
    /// <para>
    /// 引号与白名单是两道独立的门，不是二选一：白名单（<see cref="GuardDesiredIdentifier"/>）
    /// 只管**期望结构**里我们自己产出的名字，而 <c>existing</c> 侧的表名是真正的第三方输入
    /// （来自 information_schema）、内容不可控，只能靠这里的引号保证安全。
    /// </para>
    /// </summary>
    private static string Qualified(string tableName) => $"{DataSchema}.\"{tableName}\"";

    /// <summary>
    /// 标识符校验。分两档，对应两个来源与两种插入方式。
    /// <para>
    /// <b>① 期望表名（<c>desired</c>）→ 白名单 <c>[A-Za-z0-9_]</c>。</b>
    /// 表名是唯一**以裸标识符形式**拼进 DDL 的对象（见 <see cref="Qualified"/> 的注释：
    /// 它此前连引号都没有）。规格对 <c>conn_code</c> 有"仅 <c>[a-z0-9_]</c>"的约束，
    /// 而 <c>table_name</c> 在界面章节里是用户可编辑字段、Core 内此前没有任何校验，
    /// 故这里补上白名单 —— 这是**纵深防御的第二道门**，不是唯一防线（第一道是引号）。
    /// </para>
    /// <para>
    /// <b>② 期望列名 → 与现存标识符同档</b>（引号 + 拦引号/反斜杠/控制字符/非 ASCII）。
    /// 列名在 DDL 里**全程有双引号包裹**（建表、加列、rename、drop 都是），
    /// 故不需要白名单：PG 允许 <c>"wen du"</c>、<c>"my-col"</c> 这类带引号的合法标识符，
    /// 拦它们会误杀用户已有的合法列名。这条政策由"带空格与连字符的列名仍被放行"锁定。
    /// </para>
    /// <para>
    /// <b>③ 数据库现存结构（<c>existing</c>）→ 只拦"会破坏引号包裹"的字符</b>
    /// （引号、反斜杠、控制字符），**不做白名单**。这是**真正的第三方输入**
    /// （来自 information_schema），内容不可控：别人建的 <c>"my-table"</c> 合法且安全，
    /// 安全由 <see cref="Qualified"/> 的引号保证。
    /// </para>
    /// <para>
    /// ⚠️ <b>只校验真的会进 SQL 的对象</b>。<c>existing</c> 里的孤儿表在
    /// <c>DropRemovedTables=false</c>（默认）时**零产出**，其表名与列名一个字符都不会进 DDL；
    /// 若把它们的列名也扫一遍，库里一张配置外的遗留表只要有一列叫 <c>温_du</c>，
    /// 用户就**连预览都拿不到** —— 那是过度守卫，且守卫错了对象。
    /// 孤儿表只在 <c>DropRemovedTables=true</c> 时校验**表名**（那时才会生成 <c>drop table</c>），
    /// 其列名永远不进 SQL，故永不校验。
    /// </para>
    /// <para>
    /// ⚠️ <b>刻意不在这里重复"长度"与"重复"校验</b>：Task 4 的 <c>GuardColumns</c> 已对期望结构的
    /// 全部列（手工名与自动名一视同仁）拦下超 63 字节与重复，两处实现会形成漂移。
    /// 长度只在生成侧起作用：<see cref="SoftDeletedName"/> 的字节预算与
    /// <see cref="IndexName"/> 的索引名预算。
    /// </para>
    /// </summary>
    /// <param name="desired">期望结构（长度与重复校验已由 Task 4 完成）</param>
    /// <param name="existing">数据库现存结构（第三方输入；只查会破坏引号包裹的字符）</param>
    /// <param name="existingByName">已判定无大小写等价冲突的现存表索引</param>
    /// <param name="desiredNames">期望表名集合（判定哪些现存表是孤儿）</param>
    /// <param name="options">迁移选项（决定孤儿表是否会被 DROP）</param>
    /// <exception cref="InvalidOperationException">标识符为空、越出白名单、或含引号/反斜杠/控制字符/非 ASCII。</exception>
    private static void GuardIdentifiers(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing,
        IReadOnlyDictionary<string, ExistingTable> existingByName,
        IReadOnlySet<string> desiredNames,
        MigrationOptions options)
    {
        // ① 期望表名：白名单。这是唯一以裸标识符拼进 DDL 的名字。
        foreach (var table in desired.Tables)
        {
            GuardDesiredTableName(table.TableName);
        }

        // ② 期望列名：有引号包裹，故与现存标识符同档（不要求白名单）。
        foreach (var table in desired.Tables)
        {
            foreach (var column in table.Columns)
            {
                GuardQuotedIdentifier(table.TableName, column.Name, "列名");
            }
        }

        // ③ 现存结构：只校验会被引用的那部分。
        foreach (var table in existing)
        {
            var isOrphan = !desiredNames.Contains(table.TableName);

            if (isOrphan)
            {
                // 孤儿表：默认选项下什么都不做，其表名与列名都不会进 SQL。
                // 只有 DropRemovedTables=true 时会生成 drop table，那时才校验表名。
                // 列名永不校验 —— 孤儿表的列名在任何分支下都不进 SQL。
                if (options.DropRemovedTables)
                {
                    GuardQuotedIdentifier(table.TableName, table.TableName, "数据库现有表名");
                }

                continue;
            }

            // 会被比对的表：表名与列名都会进 DDL（add column / rename column / drop column）。
            // 用 existingByName 取出真正会被规划到的那一张（大小写等价的那张）。
            var planned = existingByName[table.TableName];

            GuardQuotedIdentifier(planned.TableName, planned.TableName, "数据库现有表名");

            foreach (var column in planned.Columns)
            {
                GuardQuotedIdentifier(planned.TableName, column.Name, "数据库现有列名");
            }
        }
    }

    /// <summary>
    /// 期望表名的白名单：<c>[A-Za-z0-9_]</c>、非空。
    /// 表名会以裸标识符形式拼进 DDL（<c>d.表名</c>），故这里要求它落在安全字符集内。
    /// </summary>
    /// <exception cref="InvalidOperationException">为空或含白名单外字符。</exception>
    private static void GuardDesiredTableName(string tableName)
    {
        GuardQuotedIdentifier(tableName, tableName, "表名");

        foreach (var ch in tableName)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
            {
                throw new InvalidOperationException(
                    $"表名 {tableName} 含非法字符 '{ch}'。表名只允许 ASCII 字母、数字与下划线" +
                    "（与 conn_code / group_code 的约束一致，规格 3.2 节）。" +
                    "空格、连字符、分号、括号等一律拒绝：表名会作为标识符拼进 DDL，" +
                    "这些字符在未加引号时是语法元素。请修改该采集组的目标表名。");
            }
        }
    }

    /// <summary>
    /// "会被双引号包裹"的标识符的校验：非空、非 ASCII、无引号/反斜杠/控制字符。
    /// <para>
    /// 不要求白名单：加引号后 PG 允许 <c>"wen du"</c> / <c>"my-table"</c> 这类合法标识符，
    /// 拦它们会误杀用户已有的合法结构（本任务的政策由控制器裁定）。
    /// 安全由调用点加的引号保证，字符集检查只用来挡住"会破坏引号包裹"与"非 ASCII"两类。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">为空、含非 ASCII、引号、反斜杠或控制字符。</exception>
    private static void GuardQuotedIdentifier(string tableName, string identifier, string role)
    {
        if (identifier.Length == 0)
        {
            throw new InvalidOperationException(
                $"表 {tableName} 的{role}为空。空标识符会让 DDL 里出现一对空引号而报语法错误，" +
                "请修正该名称（列的 ColumnName 留空表示「由显示名自动生成」，不应产出空名）。");
        }

        foreach (var ch in identifier)
        {
            if (ch is '"' or '\\')
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的{role} {identifier} 含需转义字符（引号或反斜杠），" +
                    "直接拼接进 DDL 会构成 SQL 注入。请修改该名称，不要使用引号或反斜杠。");
            }

            if (char.IsControl(ch))
            {
                throw new InvalidOperationException(
                    $"表 {tableName} 的{role} {identifier} 含控制字符，直接拼接进 DDL 会构成 SQL 注入。" +
                    "请修改该名称，只使用可见字符。");
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
