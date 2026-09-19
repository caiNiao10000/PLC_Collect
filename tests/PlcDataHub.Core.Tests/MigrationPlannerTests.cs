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
            .Should().Contain("create table if not exists d.plc01_fast")
            .And.Contain("\"ts\" timestamp not null")
            .And.Contain("\"wen_du\" double precision null");
        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.CreateIndex);
    }

    /// <summary>
    /// 跨任务约束 3：规格 3.4 节要求 <c>q smallint not null default 0</c>，而
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

        create.Should().Contain("\"q\" smallint not null default 0");

        // 反向：default 0 只能出现在质量列那一段，不能漏到别的列上
        create.Split(",\n").Should().ContainSingle(line => line.Contains("default", StringComparison.Ordinal))
            .Which.Should().Contain("\"q\"");
    }

    [Fact]
    public void 建表语句含全部期望列且带时间索引()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        // 实测形态：create table if not exists d.plc01_fast (\n  "ts" …,\n  "q" …,\n  "src_ts" …,\n  "wen_du" …\n)
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql.Should().Be(
            "create table if not exists d.plc01_fast (\n" +
            "  \"ts\" timestamp not null,\n" +
            "  \"q\" smallint not null default 0,\n" +
            "  \"src_ts\" timestamp null,\n" +
            "  \"wen_du\" double precision null\n" +
            ")");
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateIndex).Sql.Should().Be(
            "create index if not exists plc01_fast_ts_idx on d.plc01_fast (ts)");
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
            .Should().Be("alter table d.plc01_fast add column if not exists \"ya_li\" double precision null");
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
            "alter table d.plc01_fast add column if not exists \"q\" smallint not null default 0",
            "alter table d.plc01_fast add column if not exists \"src_ts\" timestamp null");
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
            .Should().StartWith("alter table d.plc01_fast rename column \"jiu_dian\" to \"deleted_jiu_dian_");
    }

    [Fact]
    public void 软删除列名带当天日期后缀()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var sql = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions)
            .Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;

        sql.Should().Be(
            $"alter table d.plc01_fast rename column \"jiu_dian\" to \"deleted_jiu_dian_{DateTime.Now:yyyyMMdd}\"");
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
        drop.Sql.Should().Be("alter table d.plc01_fast drop column \"jiu_dian\"");
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

        message.Should().Contain("d.plc01_fast").And.Contain("不会自动修改列类型")
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
            "alter table d.plc01_fast add column if not exists \"ya_li\" double precision null");
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

        drop.Sql.Should().Be("drop table d.plc01_fast");
        drop.Description.Should().Contain("d.plc01_fast").And.Contain("86,400");
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
    /// 反向对照：约束 4 只要求拦"非 ASCII"与"内嵌引号"，**不是**"白名单只许 [a-z0-9_]"。
    /// 空格、连字符、大写字母在双引号标识符里都合法且安全，必须放行 ——
    /// 误杀它们会让用户已有的合法表结构无法规划迁移。
    /// </summary>
    [Theory]
    [InlineData("wen_du")]
    [InlineData("Wen_Du")]
    [InlineData("wen du")]
    [InlineData("wen-du")]
    [InlineData("wen.du")]
    [InlineData("_wen_du_")]
    [InlineData("wen$du")]
    public void 安全的_ASCII_手工列名一律放行(string columnName)
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (columnName, "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql
            .Should().Contain($"\"{columnName}\" double precision null");
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
            "create table if not exists d.plc01_fast (\n" +
            "  \"ts\" timestamp not null,\n" +
            "  \"q\" smallint not null default 0,\n" +
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

    [Fact]
    public void 仓库里存在仅大小写不同的重复列时不抛无关异常()
    {
        // 加引号建过表的库里可能出现 "Wen_Du" 与 "wen_du" 并存。
        // 大小写不敏感比对下两者是同一个键，若用 ToDictionary 会抛"已添加相同键的项"——
        // 那是与本方法语义无关的异常。期望行为：取先出现的一列，正常规划。
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("Wen_Du", "double precision"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue();
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
    /// </summary>
    private static DesiredSchema Table(string name, params (string Name, string Type)[] columns) =>
        new(new[]
        {
            new DesiredTable(name, columns
                .Select(c => new DesiredColumn(
                    c.Name,
                    c.Type,
                    IsNullable: !c.Name.Equals(DesiredSchema.TimestampColumn, StringComparison.Ordinal)
                                && !c.Name.Equals(DesiredSchema.QualityColumn, StringComparison.Ordinal)))
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
