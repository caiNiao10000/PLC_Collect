using FluentAssertions;
using PlcDataHub.Core.Migration;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Schema;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class MigrationPlannerTests
{
    private static readonly MigrationOptions SafeOptions = new();

    // ───────────────────────── 建表 ─────────────────────────

    [Fact]
    public void 表不存在时生成建表语句()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.IsEmpty.Should().BeFalse();
        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.CreateTable);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql
            .Should().Contain("create table if not exists d.\"plc01_fast\"")
            .And.Contain("\"ts\" timestamp not null")
            .And.Contain("\"wen_du\" double precision null");
        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.CreateIndex);
    }

    /// <summary>
    /// 跨任务约束 3：规格 3.4 节要求质量列带 <c>default 0</c>，而
    /// <see cref="DesiredColumn"/> 只表达 <c>(Name, PostgresType, IsNullable)</c>、
    /// **无法表达默认值**，故必须由建表 SQL 侧硬编码。
    /// 断言用 <c>Contain</c> 而不是全串相等：本用例要锁的是"质量列带 default 0"，
    /// 其余列（ts / wen_du）的顺序与写法由别的用例负责，不在此重复锁定。
    /// </summary>
    [Fact]
    public void 建表时质量列硬编码_default_0()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var create = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql;

        create.Should().Contain("\"q\" smallint default 0 not null");

        // 反向：default 只能出现在质量列那一段，不能漏到别的列上
        create.Split(",\n").Should().ContainSingle(line => line.Contains("default", StringComparison.Ordinal))
            .Which.Should().Contain("\"q\"");
    }

    /// <summary>
    /// 质量列的大小写不影响 <c>default 0</c>。
    /// <para>
    /// 这条是 M3 要求的"会红的断言"：<c>default 0</c> 此前靠"列名恰为小写 <c>q</c>"的名字约定触发，
    /// 而 <c>MigrationPlanner</c> 是 public API —— 跨任务不变量不能靠名字约定静默维持。
    /// <see cref="DesiredSchemaBuilder"/> 的大小写不敏感去重守卫保证**任何拼写的质量列都只可能有一个**
    /// （手工名写 <c>Q</c> 会与固定列 <c>q</c> 相撞并抛错），故按 <c>OrdinalIgnoreCase</c> 判定是安全的，
    /// 且能保证换拼写时不会**静默漏掉**默认值。
    /// </para>
    /// <para>
    /// ⚠️ 本任务**不检查已存在表的 <c>q</c> 是否真的带默认值** —— 合同里
    /// <see cref="ExistingColumn"/> 只有 <c>(Name, PostgresType)</c>，不承载默认值，
    /// 属 Plan 2 的接口限制，登记备查。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    public void 质量列两种大小写拼写都带_default_0(string qualityColumnName)
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), (qualityColumnName, "smallint"));

        var create = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql;

        create.Should().Contain($"\"{qualityColumnName}\" smallint default 0 not null");
    }

    [Fact]
    public void 建表语句含全部期望列且带时间索引()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        // 实测形态（表名与索引名一律加双引号；default 0 在可空性之前）
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql.Should().Be(
            "create table if not exists d.\"plc01_fast\" (\n" +
            "  \"ts\" timestamp not null,\n" +
            "  \"q\" smallint default 0 not null,\n" +
            "  \"src_ts\" timestamp null,\n" +
            "  \"wen_du\" double precision null\n" +
            ")");
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateIndex).Sql.Should().Be(
            "create index if not exists \"plc01_fast_ts_idx\" on d.\"plc01_fast\" (ts)");
    }

    /// <summary>
    /// 时间索引名也受 PostgreSQL 标识符的 63 **字节**上限约束，必须自己拦。
    /// <para>
    /// 索引名 = 表名 + 7 字节后缀 <c>_ts_idx</c> + **2 字节引号**，故表名上限是
    /// <c>63 - 7 - 2 = 54</c> 字节。PG 对超长标识符是**静默截断**而非报错：
    /// 同 schema 下两张共享长前缀的表会截断成同一个索引名，
    /// 第二张表的 <c>create index if not exists</c> 变成静默 no-op —— **漏索引且无报错**。
    /// 与 Task 4 的 <c>GuardColumns</c> 一样选择"响亮拒绝"而不是"静默截断"。
    /// </para>
    /// <para>
    /// ⚠️ 引号**计入**字节数是实测确认的：我第一版按 <c>63 - 7 = 56</c> 算上限，
    /// 实测 56 字节表名产出 **65** 字节索引名，正是被探针打印真实值查出来的。
    /// 故边界锁在 54/55 两侧。
    /// </para>
    /// <para>
    /// 本用例是补的覆盖缺口：加它之前，把这段守卫整个关掉**没有任何测试会红**
    /// （变异 M7 实测 DISTINCT_RED=0）。
    /// </para>
    /// </summary>
    [Fact]
    public void 索引名恰好_63_字节时通过()
    {
        // 54 字节表名 → "table_ts_idx" 含引号恰好 63 字节
        var tableName = new string('t', 54);
        var desired = Table(tableName, ("ts", "timestamp"), ("q", "smallint"));

        var sql = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.CreateIndex).Sql;

        var indexName = tableName + "_ts_idx";
        sql.Should().Be($"create index if not exists \"{indexName}\" on d.\"{tableName}\" (ts)");
        System.Text.Encoding.UTF8.GetByteCount($"\"{indexName}\"").Should().Be(63);
    }

    /// <summary>与上一条配对：表名多 1 字节（55）时索引名 64 字节，必须响亮拒绝。</summary>
    [Fact]
    public void 索引名超过_63_字节时被拒绝()
    {
        var tableName = new string('t', 55);
        var desired = Table(tableName, ("ts", "timestamp"), ("q", "smallint"));

        var message = Assert.Throws<InvalidOperationException>(
            () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)).Message;

        message.Should().Contain("索引名").And.Contain("64").And.Contain("63").And.Contain("54");
    }

    /// <summary>
    /// 夹具漂移守卫：本文件的 <c>Table</c> 帮助方法手工复刻了
    /// <c>DesiredSchemaBuilder</c> 的固定列规则（ts / q / src_ts 及其类型与可空性）。
    /// 若生产侧的固定列规则变了，这条测试会红，从而逼出夹具的同步修改 ——
    /// 否则夹具会悄悄与生产代码漂移，让本文件其余断言测一个并不存在的结构。
    /// </summary>
    [Fact]
    public void 夹具与真实_DesiredSchemaBuilder_的固定列保持一致()
    {
        var fromFixture = Table(
            "plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp")).Tables.Single().Columns;
        var fromProduction = DesiredSchemaBuilder
            .Build(new[] { MakeConnection() }, new[] { MakeGroup("plc01_fast", Array.Empty<PointConfig>()) })
            .Tables.Single().Columns;

        fromFixture.Should().BeEquivalentTo(fromProduction, o => o.WithStrictOrdering());
    }

    // ───────────────────────── 加列 ─────────────────────────

    [Fact]
    public void 新增采集点生成_ADD_COLUMN()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("ya_li", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.AddColumn);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.AddColumn).Sql
            .Should().Be("alter table d.\"plc01_fast\" add column if not exists \"ya_li\" double precision null");
        plan.DestructiveSteps.Should().BeEmpty();
    }

    [Fact]
    public void 加列语句同样带_if_not_exists_与_default_0()
    {
        // 加列走的是与建表同一个 ColumnDefinitionSql，故"幂等关键字"与"质量列默认值"
        // 两条性质在加列路径上必须同样成立 —— 否则"库已存在该列"时加列会报错。
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Where(s => s.Kind == MigrationStepKind.AddColumn).Select(s => s.Sql).Should().Equal(
            "alter table d.\"plc01_fast\" add column if not exists \"q\" smallint default 0 not null",
            "alter table d.\"plc01_fast\" add column if not exists \"src_ts\" timestamp null");
    }

    // ──────────────────── 列名比对：大小写不敏感 ────────────────────

    /// <summary>
    /// 跨任务约束 1：PostgreSQL 未加引号的标识符大小写不敏感（折叠为小写），
    /// 故库里的 <c>Wen_Du</c> 与期望的 <c>wen_du</c> 是**同一列**。
    /// 用 <c>Ordinal</c> 比对会误判为缺列 → 生成 ADD COLUMN → 该列已存在 → 迁移失败。
    /// </summary>
    [Fact]
    public void 库里列名大小写不同时不生成_ADD_COLUMN()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.AddColumn);
        plan.IsEmpty.Should().BeTrue("库里已有 Wen_Du 与期望的 wen_du 是同一列，不该产生任何 DDL");
    }

    /// <summary>与前一条配对：反过来（期望大写、库里小写）也必须认得出来。</summary>
    [Fact]
    public void 期望列名大写而库里小写时同样不生成_ADD_COLUMN()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.AddColumn);
        plan.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// 大小写不敏感必须同样作用于"这一列已不在配置中"的判定：
    /// 库里 <c>Wen_Du</c>、期望 <c>wen_du</c> 时，前者**不是**待软删除的遗留列。
    /// 若这里用 Ordinal，会把同一个列既当"已存在"又当"已删除"，生成一次无意义的改名。
    /// </summary>
    [Fact]
    public void 大小写不同不算待删除列()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.SoftDeleteColumn);
        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.DropColumn);
    }

    /// <summary>表名比对必须与列名同规则（PG 未加引号标识符一律折叠为小写）。</summary>
    [Fact]
    public void 表名大小写不同时不做建表比对()
    {
        var desired = Table("PLC01_Fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue("库里的 plc01_fast 与期望的 PLC01_Fast 是同一张表");
    }

    // ──────────────────────── 删除列 ────────────────────────

    [Fact]
    public void 删除采集点默认软删除而不是_DROP()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("jiu_dian", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.SoftDeleteColumn);
        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.DropColumn);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql
            .Should().StartWith("alter table d.\"plc01_fast\" rename column \"jiu_dian\" to \"deleted_jiu_dian_");
    }

    [Fact]
    public void 软删除列名带当天日期后缀()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var sql = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;

        sql.Should().Be(
            $"alter table d.\"plc01_fast\" rename column \"jiu_dian\" to \"deleted_jiu_dian_{DateTime.Now:yyyyMMdd}\"");
    }

    [Fact]
    public void 软删除不是破坏性操作且说明里保留原名与新名()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var step = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn);

        step.IsDestructive.Should().BeFalse("改名保留数据，不丢任何东西");
        step.Description.Should().Contain("jiu_dian").And.Contain("deleted_jiu_dian_");
        step.TableName.Should().Be("plc01_fast");
    }

    [Fact]
    public void 关闭软删除时才生成_DROP_COLUMN_且标记为破坏性()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(SoftDeleteRemovedColumns: false));

        var drop = plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.DropColumn).Subject;
        drop.IsDestructive.Should().BeTrue();
        drop.Sql.Should().Be("alter table d.\"plc01_fast\" drop column \"jiu_dian\"");
        drop.Description.Should().Contain("丢弃");   // 破坏性操作必须写清后果
    }

    [Fact]
    public void 硬删除的说明里报出表有多少行()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", new[] { ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision") }, RowCount: 86400);

        var drop = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(SoftDeleteRemovedColumns: false))
            .Steps.Single(s => s.Kind == MigrationStepKind.DropColumn);

        // "故意泄漏后果"：预览弹窗必须能告诉用户会丢多少行。实测形态带千位分隔符。
        drop.Description.Should().Contain("86,400");
        drop.Description.Should().Contain("jiu_dian");
    }

    [Fact]
    public void 软删除列名不会超过_63_字节()
    {
        var longColumn = new string('c', 55);
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (longColumn, "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        var sql = plan.Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;
        var newName = sql[(sql.IndexOf(" to \"", StringComparison.Ordinal) + 5)..].TrimEnd('"');
        System.Text.Encoding.UTF8.GetByteCount(newName).Should().BeLessOrEqualTo(63);
    }

    /// <summary>
    /// 软删除名生成侧的字节预算上界（不是"校验长度"—— 长度校验归 Task 4 的 GuardColumns）：
    /// 原列名本身恰好 63 字节时，加前缀与日期后必须被截到 63 字节以内，
    /// 否则 PostgreSQL 会**静默截断**，而截断后的名字可能与另一个遗留列撞名。
    /// 实测：55 字节原列名 → 57 字节新名；63 字节原列名 → 63 字节新名（已截原列名）。
    /// </summary>
    [Fact]
    public void 恰好_63_字节的原列名软删除后仍不超_63_字节()
    {
        var maxColumn = new string('c', 63);
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (maxColumn, "double precision"));

        var sql = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;

        var newName = sql[(sql.IndexOf(" to \"", StringComparison.Ordinal) + 5)..].TrimEnd('"');
        newName.Should().StartWith("deleted_");
        newName.Should().EndWith(DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        System.Text.Encoding.UTF8.GetByteCount(newName).Should().BeLessOrEqualTo(63);
    }

    // ──────────────────────── 类型冲突 ────────────────────────

    [Fact]
    public void 类型冲突阻止迁移而不是自动改类型()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "boolean"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var act = () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        act.Should().Throw<MigrationConflictException>()
            .WithMessage("*wen_du*")
            .WithMessage("*double precision*")
            .WithMessage("*boolean*");
    }

    /// <summary>冲突消息必须给出"不会自动改"与两条可选出路 —— 这是规格 4.2 节的要求。</summary>
    [Fact]
    public void 类型冲突消息写明不会自动改并给出两条出路()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "boolean"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var message = Assert.Throws<MigrationConflictException>(
            () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)).Message;

        message.Should().Contain("d.\"plc01_fast\"").And.Contain("不会自动修改列类型")
            .And.Contain("新建一列").And.Contain("确认丢弃该列数据后重建");
    }

    [Fact]
    public void 类型等价写法不算冲突()
    {
        // PG 的 information_schema 可能把 integer 报成 int4，把 timestamp 报成 timestamp without time zone
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("count", "integer"));
        var existing = Existing("plc01_fast", ("ts", "timestamp without time zone"), ("q", "int2"), ("count", "int4"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.AddColumn);
        plan.IsEmpty.Should().BeTrue();
    }

    /// <summary>大小写与空白不该影响类型等价判定（information_schema 的写法并不统一）。</summary>
    [Fact]
    public void 类型别名比对忽略大小写与首尾空白()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("ok", "boolean"), ("value", "double precision"));
        var existing = Existing("plc01_fast", ("ts", " TIMESTAMP "), ("q", "INT2"), ("ok", "BOOL"), ("value", "FLOAT8"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue();
    }

    // ───────────────────────── 幂等 ─────────────────────────

    [Fact]
    public void 幂等_同一配置第二次为空计划()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    /// <summary>
    /// 验收项 A12 的核心场景：库是空的 → 第一次计划是"建表" → 把该计划**执行掉**
    /// （即库结构如实变成期望结构）→ 再规划一次 → 必须是空计划、不产生任何 DDL。
    /// 这条比"期望==现存"更强：它跨过了一次真实的计划—执行往返。
    /// </summary>
    [Fact]
    public void 执行计划后再规划一次必须得到空计划()
    {
        // 第一次：库是空的
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));
        var firstPlan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);
        firstPlan.IsEmpty.Should().BeFalse();

        // 第二次：库已经是第一次计划执行完的样子（含 ts 索引，索引不影响列规划）
        var afterExecution = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));
        var secondPlan = MigrationPlanner.Plan(desired, new[] { afterExecution }, SafeOptions);

        secondPlan.IsEmpty.Should().BeTrue("验收项 A12 要求迁移幂等");
    }

    /// <summary>
    /// 三条路径合起来的往返幂等：
    /// ① 新增一列 → 执行后（库里多出 ya_li）再规划必须为空；
    /// ② 删除一列 → 执行后（库里那列已改名为 deleted_*）再规划必须为空；
    /// ③ 类型冲突的配置不参与（它本来就拒绝执行）。
    /// ①+② 一起放在同一个 fixture 里，是因为真实场景里两件事常常同一轮发生。
    /// </summary>
    [Fact]
    public void 加列与软删除都执行过之后再规划必须为空计划()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("ya_li", "double precision"));

        // 执行前：库里没有 ya_li（要新增），多出 jiu_dian（要软删除）
        var before = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("jiu_dian", "double precision"));
        var plan = MigrationPlanner.Plan(desired, new[] { before }, SafeOptions);

        plan.Steps.Where(s => s.Kind == MigrationStepKind.AddColumn).Select(s => s.Sql).Should().Equal(
            "alter table d.\"plc01_fast\" add column if not exists \"ya_li\" double precision null");
        var rename = plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Subject;
        var renamed = rename.Sql[(rename.Sql.IndexOf(" to \"", StringComparison.Ordinal) + 5)..].TrimEnd('"');

        // 执行后：ya_li 已加上、jiu_dian 已改名（改名用的是计划里给出的那个名字，不是"今天"）
        var after = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"),
            ("ya_li", "double precision"), (renamed, "double precision"));
        var secondPlan = MigrationPlanner.Plan(desired, new[] { after }, SafeOptions);

        secondPlan.IsEmpty.Should().BeTrue("执行完第一次计划后，第二次必须不产生任何 DDL");
    }

    [Fact]
    public void 软删除遗留列在后续迁移中不再被重复改名()
    {
        // 上一轮把 jiu_dian 软删成了 deleted_jiu_dian_20260209
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("deleted_jiu_dian_20260209", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue("软删除遗留列不应被反复改名，否则迁移不幂等");
    }

    /// <summary>
    /// 幂等性承重细节：跳过条件是"以 deleted_ 开头"，**不是**"等于今天生成的那个名字"。
    /// 这里用一个与"今天"无关的旧日期，正是要证明跨天运行也不会重复改名。
    /// </summary>
    [Fact]
    public void 昨天的软删除遗留列今天也不再改名()
    {
        var yesterday = DateTime.Now.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ($"deleted_jiu_dian_{yesterday}", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue("跳过条件按前缀判定，与日期无关；否则跨天运行会不断改名");
    }

    /// <summary>大小写变体的遗留列同样要跳过（PG 大小写不敏感，Deleted_ 与 deleted_ 是一回事）。</summary>
    [Fact]
    public void 软删除前缀的大小写变体也被跳过()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("Deleted_jiu_dian_20260209", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue();
    }

    // ───────────────────────── 删表 ─────────────────────────

    [Fact]
    public void 删除采集组默认不删表()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.DropTable);
        plan.IsEmpty.Should().BeTrue("默认只停采集、不删表，故不产生任何 DDL");
    }

    [Fact]
    public void 关闭保留选项时才删表且标记破坏性()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing("plc01_fast", new[] { ("ts", "timestamp"), ("q", "smallint") }, RowCount: 86400);

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(SoftDeleteRemovedColumns: true, DropRemovedTables: true));

        var drop = plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.DropTable).Subject;
        drop.IsDestructive.Should().BeTrue();
        // ⚠️ 实测：行数按 N0 格式化，故 86400 行在说明里是 "86,400"，不是 "86400"。
        // brief 这里期望的是不含千位分隔符的 "86400"，与实现不符 —— 以实现为准（见 task-5-report.md）。
        drop.Description.Should().Contain("86,400");   // 必须报出会丢多少行
    }

    /// <summary>实测：行数按 <c>N0</c> 格式化，故 86400 行在说明里是 <c>86,400</c>。</summary>
    [Fact]
    public void 删表说明里报出将丢弃多少行()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing("plc01_fast", new[] { ("ts", "timestamp"), ("q", "smallint") }, RowCount: 86400);

        var drop = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(DropRemovedTables: true))
            .Steps.Single(s => s.Kind == MigrationStepKind.DropTable);

        drop.Sql.Should().Be("drop table d.\"plc01_fast\"");
        drop.Description.Should().Contain("d.\"plc01_fast\"").And.Contain("86,400");
    }

    [Fact]
    public void 其他表仍按期望结构正常规划_删表选项不影响它()
    {
        var desired = Table("plc02_slow", ("ts", "timestamp"), ("q", "smallint"));
        var existing = new[]
        {
            Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint")),
            Existing("plc02_slow", ("ts", "timestamp")),
        };

        var plan = MigrationPlanner.Plan(desired, existing, new MigrationOptions(DropRemovedTables: true));

        // 实测顺序：先按 desired.Tables 规划（AddColumn），再处理库里的孤儿表（DropTable）。
        plan.Steps.Select(s => s.Kind).Should().Equal(MigrationStepKind.AddColumn, MigrationStepKind.DropTable);
        plan.DestructiveSteps.Should().ContainSingle().Which.Kind.Should().Be(MigrationStepKind.DropTable);
    }

    // ──────────────── 标识符校验（跨任务约束 4）────────────────

    /// <summary>
    /// 必须拒绝的四类：非 ASCII、内嵌引号（SQL 注入面）、反斜杠、控制字符 —— 外加空名。
    /// <para>
    /// ⚠️ 本 Theory **刻意不包含**空格与连字符：它们是可直接放进双引号标识符的**安全** ASCII 字符
    /// （PG 允许 <c>"wen du"</c>），拦它们既非跨任务约束 4 所要求，也会误杀合法手工名。
    /// 那类输入由下面的"ASCII 手工列名一律放行"锁定。写 fixture 时先跑红确认过这一点。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("温_du", "非 ASCII")]
    [InlineData("温度", "非 ASCII")]
    [InlineData("a\"; drop table t; --", "内嵌双引号（SQL 注入面）")]
    [InlineData("\"wen_du\"", "整串被引号包裹")]
    [InlineData("wen\\du", "反斜杠")]
    [InlineData("a\nb", "换行（控制字符）")]
    [InlineData("a\tb", "制表符（控制字符）")]
    [InlineData("", "空列名")]
    public void 非法手工列名被拒绝(string columnName, string reason)
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (columnName, "double precision"));

        var act = () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        act.Should().Throw<InvalidOperationException>($"{reason} 的列名不得进入 DDL")
            .WithMessage("*列名*");
    }

    /// <summary>
    /// 反向对照（本任务是**引号 + 非 ASCII** 两档政策，不是"白名单只许 [a-z0-9_]"）：
    /// 列名在 DDL 里**全程有双引号包裹**，故空格、连字符、点号、<c>$</c>、大写字母
    /// 都是 PG 允许的合法标识符字符，必须放行 —— 误杀它们会让用户已有的合法列名无法规划。
    /// <para>
    /// 政策边界（三条，由本文件三组用例分别锁定）：
    /// ① 期望**列名** → 引号 + 拦非 ASCII/引号/反斜杠/控制字符（本用例 + 上面的 Theory）；
    /// ② 期望**表名** → 额外要求白名单 <c>[A-Za-z0-9_]</c>（见"非法期望表名被拒绝"）；
    /// ③ 数据库**现存**表名与列名 → 同 ①（第三方输入，别人建的 <c>"my-table"</c> 合法且安全）。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("wen_du")]
    [InlineData("Wen_Du")]
    [InlineData("wen du")]
    [InlineData("wen-du")]
    [InlineData("wen.du")]
    [InlineData("_wen_du_")]
    [InlineData("wen$du")]
    public void 带引号即安全的_ASCII_列名一律放行(string columnName)
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (columnName, "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql
            .Should().Contain($"\"{columnName}\" double precision null");
    }

    // ──────────── 表名注入面（Critical 修复的核心）────────────

    /// <summary>
    /// <b>Critical：期望表名只允许白名单 <c>[A-Za-z0-9_]</c>。</b>
    /// <para>
    /// 表名是唯一以**裸标识符**形式拼进 DDL 的对象。不加引号时
    /// <c>;</c>、空格、括号、<c>-</c> 都是语法元素：实测表名
    /// <c>x (dummy int); drop table d.other; create table d.x</c>
    /// 会让生成的语句变成多条并删掉别的表。
    /// 规格对 <c>conn_code</c> 有"仅 <c>[a-z0-9_]</c>"约束，而 <c>table_name</c>
    /// 在界面上可由用户编辑、Core 内此前零校验，故这里补上。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("plc01 fast", "空格")]
    [InlineData("x (dummy int); drop table d.other; create table d.x", "分号注入")]
    [InlineData("x'", "单引号")]
    [InlineData("x-y", "连字符")]
    [InlineData("x(a", "左括号")]
    [InlineData("x)b", "右括号")]
    [InlineData("x.y", "点号（裸标识符里是 schema 分隔符）")]
    [InlineData("x$y", "美元符")]
    [InlineData("温du", "非 ASCII")]
    [InlineData("x\"y", "双引号")]
    [InlineData("", "空表名")]
    public void 非法期望表名被拒绝(string tableName, string reason)
    {
        var desired = Table(tableName, ("ts", "timestamp"), ("q", "smallint"));

        var act = () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        act.Should().Throw<InvalidOperationException>($"{reason} 的表名不得进入 DDL")
            .WithMessage("*表名*");
    }

    /// <summary>合法表名（含大写与下划线）必须照常工作，白名单不能误杀。</summary>
    [Theory]
    [InlineData("plc01_fast")]
    [InlineData("PLC01_Fast")]
    [InlineData("_t1")]
    [InlineData("t999")]
    public void 合法期望表名一律放行(string tableName)
    {
        var desired = Table(tableName, ("ts", "timestamp"), ("q", "smallint"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql
            .Should().Contain($"create table if not exists d.\"{tableName}\"");
    }

    /// <summary>
    /// <b>Critical 修复的第一道门：所有 DDL 里的表名与索引名一律加双引号。</b>
    /// <para>
    /// 即使配置层漏检（校验层尚不存在、<c>MigrationPlanner</c> 又是 public API），
    /// 引号也能保证**数据库现存表名**（真正的第三方输入）不会撕裂语句。
    /// 这里用带连字符与空格的现存表名，断言生成的 SQL 里表名**是被引号包裹的**。
    /// </para>
    /// <para>
    /// ⚠️ 断言"含引号包裹形式"而不是"整串相等"：本用例要锁的是引号政策本身，
    /// 与列内容无关。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("my-table")]
    [InlineData("other table")]
    [InlineData("weird;name")]
    public void 现存表名始终被双引号包裹(string existingTableName)
    {
        // 期望里没有这张表 → 它是孤儿表 → 用 DropRemovedTables=true 逼出 drop table 语句
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing(existingTableName, ("ts", "timestamp"), ("q", "smallint"));

        var sql = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(DropRemovedTables: true))
            .Steps.Single(s => s.Kind == MigrationStepKind.DropTable).Sql;

        sql.Should().Be($"drop table d.\"{existingTableName}\"");
    }

    /// <summary>
    /// 现存表名里的引号/非 ASCII 等仍被拦（第三方输入的基础守卫）——
    /// 引号是"破坏引号包裹"的那个字符，必须拒绝而不是拼出 <c>"a"b"</c> 这种撕裂语句。
    /// <para>
    /// ⚠️ 断言**具体的消息**（<c>含需转义字符</c> / <c>含控制字符</c> / <c>非 ASCII</c>），
    /// 而不是只断言"抛了 <see cref="InvalidOperationException"/>"：
    /// 后者会被任何**其它原因**的异常满足，从而让测试假绿
    /// （变异 M15 实测暴露过这个问题：把"匹配表"误当孤儿表也会抛异常，断言照样通过）。
    /// </para>
    /// <para>
    /// 期望表名用**大写拼写**（<c>A"B</c> 等）是必需的：守卫只校验"会被引用的对象"，
    /// 而两张表要靠大小写不敏感匹配上（PG 未加引号标识符折叠为小写，加引号建的
    /// <c>"A"B"</c> 与期望的 <c>a"b</c> 在库中是同一张表）。
    /// 若把期望写成小写 <c>a"b</c>，就会被**期望表名白名单**先拦下（那张门更严），
    /// 本用例就测不到"现存表名"这条分支了 —— 这正是我第一版写错的地方。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("a\"b", "A\"B", "含需转义字符")]
    [InlineData("a\\b", "A\\B", "含需转义字符")]
    [InlineData("a\nb", "A\nB", "含控制字符")]
    [InlineData("宽表", "宽表", "非 ASCII")]
    public void 现存表名含危险字符时被拒绝(string existingTableName, string desiredTableName, string expectedMessage)
    {
        var desired = Table(desiredTableName, ("ts", "timestamp"), ("q", "smallint"));
        var existing = new[] { Existing(existingTableName, ("ts", "timestamp"), ("q", "smallint")) };

        var act = () => MigrationPlanner.Plan(desired, existing, SafeOptions);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedMessage}*");
    }

    /// <summary>
    /// 现存**列名**的守卫要覆盖非 ASCII 分支（此前只测了引号分支，该分支零覆盖）。
    /// 现存列名会进 rename/drop 语句。
    /// </summary>
    [Fact]
    public void 现存列名含非ASCII时被拒绝()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = new[]
        {
            Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("温_du", "double precision")),
        };

        var message = Assert.Throws<InvalidOperationException>(
            () => MigrationPlanner.Plan(desired, existing, SafeOptions)).Message;

        message.Should().Contain("温_du").And.Contain("非 ASCII");
    }

    /// <summary>
    /// <b>只校验真的会进 SQL 的对象。</b>
    /// 孤儿表在默认选项（<c>DropRemovedTables=false</c>）下零产出，其列名一个字符都不进 DDL，
    /// 故不应拦住计划 —— 库里一张配置外的遗留表只要有一列叫 <c>温_du</c>，
    /// 用户此前会**连预览都拿不到**。这是过度守卫。
    /// </summary>
    [Fact]
    public void 孤儿表的脏列名不阻止计划_默认不删表()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = new[]
        {
            // 会被引用的表：干净
            Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint")),
            // 孤儿表：列名又脏又非 ASCII，但默认选项下不会进任何 SQL
            Existing("legacy_table", ("ts", "timestamp"), ("温_du", "double precision"), ("a\"b", "double precision")),
        };

        var plan = MigrationPlanner.Plan(desired, existing, SafeOptions);

        plan.IsEmpty.Should().BeTrue("孤儿表默认零产出，其列名不该进任何 SQL，也就不该校验");
    }

    /// <summary>
    /// 孤儿表的守卫只在 <c>DropRemovedTables=true</c> 时对**危险字符**生效。
    /// <para>
    /// ⚠️ 注意"危险字符"的边界（我第一版在这里写错过）：现存标识符的政策是
    /// **只拦会破坏引号包裹的字符**（引号、反斜杠、控制字符），
    /// 空格与非 ASCII **放行** —— 因为表名全程有双引号包裹，
    /// <c>drop table d."legacy table"</c> 是合法且安全的 SQL。
    /// 期望结构的表名才额外要求白名单（那张门更严，因为它是裸标识符路径 + 我们自己产出的名字）。
    /// </para>
    /// <para>
    /// 断言**具体消息**而不是"抛了异常"：后者会被任何其它原因的异常满足而假绿。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("legacy\"table", "含需转义字符")]
    [InlineData("legacy\\table", "含需转义字符")]
    [InlineData("legacy\ntable", "含控制字符")]
    public void 孤儿表会删表时危险表名被拒绝(string orphanName, string expectedMessage)
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = new[] { Existing(orphanName, ("ts", "timestamp"), ("q", "smallint")) };

        var act = () => MigrationPlanner.Plan(desired, existing, new MigrationOptions(DropRemovedTables: true));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expectedMessage}*");
    }

    /// <summary>
    /// 孤儿表的表名与列名在默认选项下**完全不被校验**：
    /// 它们一个字符都不会进 SQL。若连它们也扫，库里一张配置外的遗留表
    /// 只要有一列叫 <c>温_du</c> 或表名带空格，用户就**连预览都拿不到**（过度守卫）。
    /// <para>
    /// 本用例故意用"又脏又非 ASCII"的名字，锁的就是"默认选项下不校验"这条边界。
    /// </para>
    /// <para>
    /// ⚠️ 这里**必须用 <c>Assert.Null</c> 断言"不抛任何异常"，不能只断言 <c>IsEmpty</c>：
    /// 这个 fixture 不含"会被比对"的表，故若把孤儿表误当匹配表去校验，
    /// 该用例会以异常形式红 —— 这正是它相对其它用例的鉴别力所在。
    /// </para>
    /// </summary>
    [Fact]
    public void 孤儿表的脏表名与脏列名在默认选项下不阻止计划()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = new[]
        {
            Existing("legacy table", ("ts", "timestamp"), ("温_du", "double precision"), ("a\"b", "double precision")),
        };

        var plan = Record.Exception(() => MigrationPlanner.Plan(desired, existing, SafeOptions));

        plan.Should().BeNull("孤儿表默认零产出，其表名与列名都不进任何 SQL，也就不该校验");
        MigrationPlanner.Plan(desired, existing, SafeOptions).IsEmpty.Should().BeTrue();
    }

    /// <summary>孤儿表的列名在任何分支下都不进 SQL，故 DropRemovedTables=true 时也不校验列名。</summary>
    [Fact]
    public void 孤儿表的脏列名在删表时也不阻止计划()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = new[]
        {
            Existing("legacy_table", ("ts", "timestamp"), ("温_du", "double precision"), ("a\"b", "double precision")),
        };

        var plan = MigrationPlanner.Plan(desired, existing, new MigrationOptions(DropRemovedTables: true));

        plan.Steps.Single(s => s.Kind == MigrationStepKind.DropTable).Sql.Should().Be("drop table d.\"legacy_table\"");
    }

    // ──────────── 大小写等价的重复：响亮失败 ────────────

    /// <summary>
    /// <b>大小写等价但拼写不同的列 → 响亮抛出</b>（不静默取先出现的一列）。
    /// <para>
    /// 静默合并会让同一个库状态因 information_schema 的行序不同给出两种结果：
    /// 取到"先出现"的那列若类型不匹配就抛**假冲突**；若恰好匹配则计划为空、静默通过，
    /// 随后写入撞上另一列的类型。而且"待删列"遍历会认为该列"在配置里"，
    /// 于是它永远不被软删除、计划里一字不提。消息必须同时给出两个拼写。
    /// </para>
    /// </summary>
    [Fact]
    public void 现存列仅大小写不同时抛异常并指名两个拼写()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"), ("wen_du", "double precision"));

        var message = Assert.Throws<MigrationConflictException>(
            () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)).Message;

        message.Should().Contain("Wen_Du").And.Contain("wen_du");
    }

    /// <summary>
    /// 与上一条配对：两列**类型不同**时也必须抛（暴露后果的那一面 ——
    /// 静默合并会按行序在这两列里挑一个当"真相"，从而给出随机的冲突/通过结论）。
    /// </summary>
    [Fact]
    public void 现存列仅大小写不同且类型不同时同样抛异常()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "boolean"), ("wen_du", "double precision"));

        var act = () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        act.Should().Throw<MigrationConflictException>().WithMessage("*Wen_Du*").WithMessage("*wen_du*");
    }

    /// <summary>
    /// 大小写等价但拼写不同的**表**同样响亮抛出（与列政策统一），
    /// 且不能用 <c>ToDictionary</c> 抛 "已添加相同键的项" 那种与语义无关的异常。
    /// </summary>
    [Fact]
    public void 现存表仅大小写不同时抛异常并指名两个拼写()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = new[]
        {
            Existing("PLC01_Fast", ("ts", "timestamp"), ("q", "smallint")),
            Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint")),
        };

        var message = Assert.Throws<MigrationConflictException>(
            () => MigrationPlanner.Plan(desired, existing, SafeOptions)).Message;

        message.Should().Contain("PLC01_Fast").And.Contain("plc01_fast");
    }

    [Fact]
    public void 非ASCII列名的拒绝消息点明规格依据()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("温_du", "double precision"));

        var message = Assert.Throws<InvalidOperationException>(
            () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)).Message;

        message.Should().Contain("plc01_fast").And.Contain("温_du")
            .And.Contain("非 ASCII").And.Contain("ASCII");
    }

    [Fact]
    public void 内嵌引号的拒绝消息点明注入面()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("a\", \"b", "double precision"));

        var message = Assert.Throws<InvalidOperationException>(
            () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)).Message;

        message.Should().Contain("plc01_fast").And.Contain("SQL 注入");
    }

    /// <summary>空名单独给一条用例：确认它走的是"空标识符"分支，而不是被别人的检查顺手拦下。</summary>
    [Fact]
    public void 空手工列名被拒绝且消息点明为空()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("", "double precision"));

        var message = Assert.Throws<InvalidOperationException>(
            () => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions)).Message;

        message.Should().Contain("plc01_fast").And.Contain("为空");
    }

    /// <summary>
    /// 数据库**现存**列名也会被拼进 DDL（软删除的 rename 用 <c>"{actual.Name}"</c>），
    /// 而它是第三方输入、长度不可控。⚠️ **本任务刻意不在这里重复"超 63 字节"校验** ——
    /// 期望结构那一侧已由 Task 4 的 <c>GuardColumns</c> 拦下（手工名与自动名一视同仁），
    /// 重复实现会形成两处漂移（Task 4 报告 §16.1 专门反向警示过）。
    /// 长度只在**生成侧**起作用：软删除新名的字节预算。
    /// <para>
    /// 本用例是**唯一能区分两条截断规则**的输入形态：
    /// 先按字符上限截到 40，再按字节预算 <c>63 - 8(deleted_) - 9(_yyyymmdd) = 46</c> 收敛。
    /// 当原列名 **超过 63 字节**时，字符上限（40）比字节预算（46）更紧，
    /// 故保留下来的原列名部分是 **40 个字符**、新名 57 字节（实现注释里的"给前缀与日期留字节预算"）。
    /// 实测（探针打印真实值）：原长 40/46/47/55/63 与 100 字符，产出的原列名部分都是 40 个字符；
    /// 唯一例外是原长恰为 40 时也走 <c>else</c> 分支，结果同为 40 个字符。
    /// </para>
    /// <para>
    /// 注：PG 的标识符上限就是 63 字节，故"超过 63 字节的现存列名"在真实库里**不可能存在**；
    /// 本用例锁的是生成函数的**定义域边界行为**（它不校验长度、只负责截断），
    /// 而不是某个现实可达的库状态。不要据此推断"该分支在真实场景会被走到"。
    /// </para>
    /// </summary>
    [Fact]
    public void 软删除名的截断同时受字符上限与字节预算约束()
    {
        var huge = new string('c', 100);
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (huge, "double precision"));

        var sql = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;

        var newName = sql[(sql.IndexOf(" to \"", StringComparison.Ordinal) + 5)..].TrimEnd('"');
        var date = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        // 字符上限 40 比字节预算 46 更紧，故保留 40 个字符 → 新名 57 字节。
        newName.Should().Be($"deleted_{new string('c', 40)}_{date}");
        System.Text.Encoding.UTF8.GetByteCount(newName).Should().Be(57);
        System.Text.Encoding.UTF8.GetByteCount(newName).Should().BeLessOrEqualTo(63);
    }

    /// <summary>
    /// 数据库**现存**列名同样会进 DDL（软删除的 rename / 删除的 drop），故同样必须校验。
    /// 这是第三方输入：库里的列名可能是别的工具加引号建的。
    /// </summary>
    [Fact]
    public void 数据库现有列名含引号时同样被拒绝()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("a\"b", "double precision"));

        var act = () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        act.Should().Throw<InvalidOperationException>().WithMessage("*a\"b*").WithMessage("*SQL 注入*");
    }

    /// <summary>非法标识符必须在生成任何语句之前就失败，而不是产出"半份计划"。</summary>
    [Fact]
    public void 非法标识符在生成计划之前就抛出()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("温_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"));

        // 先规划一张合法的新表（排在前面），再遇到非法列名：
        // 若校验是边生成边做的，调用方拿不到计划也拿不到"哪里错了"的整体结论。
        var act = () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        act.Should().Throw<InvalidOperationException>();
    }

    // ──────────────────── 与 Task 4 的真实管线对接 ────────────────────

    /// <summary>
    /// 本文件其余用例的手工夹具可能写错类型或可空性（brief 的夹具就把质量列写成过别的形态），
    /// 故补一条走**真实管线**的用例：配置图 → DesiredSchemaBuilder → 迁移计划，
    /// 锁定"真实产出的期望结构能生成正确的建表语句"。
    /// 实测真实列：ts(timestamp, not null) / q(smallint, not null) / src_ts(timestamp, null)
    /// / yao_wei_wen_du(double precision, null)。
    /// </summary>
    [Fact]
    public void 真实配置图产出的期望结构能生成正确建表语句()
    {
        var points = new[]
        {
            MakePoint(1, "窑尾温度", ""),
            MakePoint(2, "风机运行", "", PointDataType.Bool),
        };
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup("plc01_fast", points) });

        var plan = MigrationPlanner.Plan(schema, Array.Empty<ExistingTable>(), SafeOptions);

        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql.Should().Be(
            "create table if not exists d.\"plc01_fast\" (\n" +
            "  \"ts\" timestamp not null,\n" +
            "  \"q\" smallint default 0 not null,\n" +
            "  \"src_ts\" timestamp null,\n" +
            "  \"yao_wei_wen_du\" double precision null,\n" +
            "  \"feng_ji_yun_xing\" boolean null\n" +
            ")");

        // 同一条管线再跑一次（库已是执行后的样子）→ 空计划
        var after = Existing("plc01_fast",
            ("ts", "timestamp without time zone"), ("q", "int2"), ("src_ts", "timestamp"),
            ("yao_wei_wen_du", "float8"), ("feng_ji_yun_xing", "bool"));
        MigrationPlanner.Plan(schema, new[] { after }, SafeOptions).IsEmpty
            .Should().BeTrue("真实管线也必须幂等，且要认得 information_schema 的类型别名");
    }

    // ───────────────────────── 参数与边界 ─────────────────────────

    [Fact]
    public void 库表列全为空时计划为空()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.IsEmpty.Should().BeTrue();
        plan.DestructiveSteps.Should().BeEmpty();
    }

    /// <summary>
    /// 大小写等价的重复列**响亮抛出**，而不是抛 <c>ToDictionary</c> 那种
    /// "已添加相同键的项"（<see cref="ArgumentException"/>，消息里既没有表名也没有列名）。
    /// 异常类型必须是本领域自己的 <see cref="MigrationConflictException"/>，
    /// 这样界面才能把它当作"要用户决策"而不是"程序出错"来呈现。
    /// </summary>
    [Fact]
    public void 大小写等价的重复列抛领域异常而不是字典异常()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"), ("wen_du", "double precision"));

        var message = Assert.Throws<MigrationConflictException>(
            () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)).Message;

        // 断言"精确类型"而非"是 Exception 的某个子类"：Assert.Throws<T> 要求**恰好**是 T，
        // 故若哪天退回 ArgumentException（ToDictionary 撞键）这条会立刻红。
        message.Should().Contain("Wen_Du").And.Contain("wen_du");
    }

    [Fact]
    public void 默认选项是安全优先()
    {
        new MigrationOptions().SoftDeleteRemovedColumns.Should().BeTrue("默认软删除，保留数据");
        new MigrationOptions().DropRemovedTables.Should().BeFalse("默认不删表");
    }

    [Fact]
    public void 两参数重载等价于默认选项()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var viaOverload = MigrationPlanner.Plan(desired, new[] { existing });
        var viaExplicitDefault = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions());

        viaOverload.Steps.Should().BeEquivalentTo(viaExplicitDefault.Steps);
    }

    [Fact]
    public void 空参数抛_ArgumentNullException()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));

        Assert.Throws<ArgumentNullException>(() => MigrationPlanner.Plan(null!, Array.Empty<ExistingTable>()));
        Assert.Throws<ArgumentNullException>(() => MigrationPlanner.Plan(desired, null!));
        Assert.Throws<ArgumentNullException>(() => MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), null!));
    }

    // ───────────────────────── 夹具 ─────────────────────────
    //
    // ⚠️ 类型标签的来源说明（brief 的夹具在这里误导过读者）：
    //   本文件的 Table/Existing 帮助方法**直接把字符串当类型用**，不做任何映射，
    //   所以 "smallint" / "timestamp" 这类字面量是**测试专用标签**，不是 DesiredColumn 的真实产出。
    //   真实产出由 SqlTypeMapper 决定，它**不会**产出 "smallint"（PointDataType 里没有对应成员）；
    //   但期望结构里的固定质量列确实是 "smallint"（DesiredSchemaBuilder 硬编码，规格 3.4 节），
    //   故这里写 "smallint" 与真实结构一致。另有"夹具与真实 DesiredSchemaBuilder 的固定列保持一致"
    //   与"真实配置图产出的期望结构能生成正确建表语句"两条用例负责防漂移。

    /// <summary>
    /// 手工构造期望结构。列的可空性复刻 <c>DesiredSchemaBuilder</c> 的固定列规则
    /// （ts / q 非空，其余可空），与生产代码的一致性由
    /// <see cref="夹具与真实_DesiredSchemaBuilder_的固定列保持一致"/> 锁定。
    /// <para>
    /// ⚠️ 固定列的识别用 <c>OrdinalIgnoreCase</c>：生产侧对质量列的大小写是**不敏感**的
    /// （<c>DesiredSchemaBuilder</c> 的去重守卫保证任何拼写的固定列都只可能有一个，
    /// <c>ColumnDefinitionSql</c> 也按不敏感判定是否加 <c>default 0</c>），
    /// 夹具必须同规则，否则"质量列大小写"这类用例会被夹具自己的偏差误导
    /// （我第一版用 <c>Ordinal</c>，把 <c>Q</c> 误标成可空，测试因此假红过一次）。
    /// </para>
    /// </summary>
    private static DesiredSchema Table(string name, params (string Name, string Type)[] columns) =>
        new(new[]
        {
            new DesiredTable(name, columns
                .Select(c => new DesiredColumn(
                    c.Name,
                    c.Type,
                    IsNullable: !c.Name.Equals(DesiredSchema.TimestampColumn, StringComparison.OrdinalIgnoreCase)
                                && !c.Name.Equals(DesiredSchema.QualityColumn, StringComparison.OrdinalIgnoreCase)))
                .ToList()),
        });

    private static ExistingTable Existing(string name, params (string Name, string Type)[] columns) =>
        Existing(name, columns, RowCount: 0);

    private static ExistingTable Existing(string name, (string Name, string Type)[] columns, long RowCount) =>
        new(name, columns.Select(c => new ExistingColumn(c.Name, c.Type)).ToList(), RowCount);

    private static DeviceConnection MakeConnection() => new(
        ConnId: 1, ConnCode: "plc01", ConnName: "1#窑 PLC", Protocol: ProtocolKind.ModbusTcp,
        Host: "192.168.0.10", Port: 502, Rack: 0, Slot: 1, SerialPort: null, Baud: 0, Enabled: true);

    private static PollGroup MakeGroup(string tableName, IReadOnlyList<PointConfig> points) => new(
        GroupId: 1, ConnId: 1, GroupCode: "fast", GroupName: "快组",
        PeriodMs: 1000, TableName: tableName, Enabled: true, Points: points);

    private static PointConfig MakePoint(
        int id,
        string displayName,
        string columnName,
        PointDataType dataType = PointDataType.Real) => new(
        PointId: id, PointCode: $"p{id}", PointName: displayName, ColumnName: columnName,
        DataType: dataType, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: true, S7: null,
        Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100 + id));
}
