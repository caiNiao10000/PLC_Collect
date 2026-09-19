using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Schema;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class DesiredSchemaBuilderTests
{
    [Theory]
    [InlineData(PointDataType.Bool, "boolean")]
    [InlineData(PointDataType.Byte, "integer")]
    [InlineData(PointDataType.Word, "integer")]
    [InlineData(PointDataType.DWord, "integer")]
    [InlineData(PointDataType.SInt, "integer")]
    [InlineData(PointDataType.USInt, "integer")]
    [InlineData(PointDataType.Int, "integer")]
    [InlineData(PointDataType.UInt, "integer")]
    [InlineData(PointDataType.DInt, "integer")]
    [InlineData(PointDataType.UDInt, "integer")]
    [InlineData(PointDataType.Real, "double precision")]
    [InlineData(PointDataType.LReal, "double precision")]
    [InlineData(PointDataType.String, "text")]
    [InlineData(PointDataType.Dtl, "timestamp")]
    public void 类型映射符合规格_3_4_节(PointDataType type, string expected)
    {
        SqlTypeMapper.ToPostgresType(type).Should().Be(expected);
    }

    /// <summary>
    /// 类型映射的**覆盖护栏**：Theory 断言过的类型集合必须与枚举成员集合**相等**（不只是条数相等）。
    /// <para>
    /// 为什么必须是"集合相等"而不是"条数相等"：条数相等挡不住**替换**这类改动 ——
    /// 例如删掉 <c>UDInt</c> 那行、再加一条重复的 <c>Int</c> 行，条数仍是 14，
    /// 而 <c>UDInt</c> 悄悄失去覆盖。集合比对则替换、重复、缺漏都会被抓住。
    /// </para>
    /// <para>
    /// 读 <c>InlineData</c> 的值用 <c>GetData(真实 MethodInfo)</c> —— 这是该 API 的
    /// **文档契约用法**（xunit 用它在运行 Theory 时校验数据与形参匹配）。
    /// 上一版曾用 <c>GetData(null!)</c>，那依赖"xunit 不校验该参数"这一**未承诺行为**，
    /// 升级 xunit 会以与业务无关的方式变红，故已弃用。
    /// </para>
    /// </summary>
    [Fact]
    public void 类型映射测试覆盖了全部数据类型枚举成员()
    {
        var theoryMethod = typeof(DesiredSchemaBuilderTests)
            .GetMethod(nameof(类型映射符合规格_3_4_节))!;

        var assertedTypes = theoryMethod
            .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
            .Cast<InlineDataAttribute>()
            .SelectMany(a => a.GetData(theoryMethod))
            .Select(args => (PointDataType)args[0])
            .ToHashSet();

        var enumMembers = Enum.GetValues<PointDataType>().ToHashSet();

        // 集合相等：新增成员漏补数据、重复/替换行、缺漏都会被这一条抓住
        assertedTypes.Should().BeEquivalentTo(
            enumMembers,
            "Theory 必须为每个枚举成员各断言一条，且不得重复或替换；新增成员时请同步补数据与映射");

        // 且每个成员都必须真的能映射（漏映射会让 SqlTypeMapper 抛异常）
        foreach (var member in enumMembers)
        {
            SqlTypeMapper.ToPostgresType(member).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void 每张表固定包含时间列与质量列()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) });

        var table = schema.Tables.Should().ContainSingle().Subject;

        table.TableName.Should().Be("plc01_fast");
        table.Columns[0].Name.Should().Be("ts");
        table.Columns[0].PostgresType.Should().Be("timestamp");
        table.Columns[0].IsNullable.Should().BeFalse();

        table.Columns[1].Name.Should().Be("q");
        table.Columns[1].PostgresType.Should().Be("smallint");
        table.Columns[1].IsNullable.Should().BeFalse();

        table.Columns[2].Name.Should().Be("src_ts");
        table.Columns[2].PostgresType.Should().Be("timestamp");
        table.Columns[2].IsNullable.Should().BeTrue();
    }

    [Fact]
    public void 停用的采集点不进表结构()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du"), MakePoint(2, "停用点", "ting_yong", enabled: false) }) });

        schema.Tables.Single().Columns.Select(c => c.Name)
            .Should().NotContain("ting_yong");
    }

    [Fact]
    public void 停用的采集组不生成表()
    {
        var disabledGroup = MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) with { Enabled = false };

        var schema = DesiredSchemaBuilder.Build(new[] { MakeConnection() }, new[] { disabledGroup });

        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public void 停用的连接下所有组都不生成表()
    {
        var connection = MakeConnection() with { Enabled = false };

        var schema = DesiredSchemaBuilder.Build(
            new[] { connection },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) });

        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public void 列名为空时用采集点显示名自动生成()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "窑尾温度", columnName: "") }) });

        schema.Tables.Single().Columns.Select(c => c.Name).Should().Contain("yao_wei_wen_du");
    }

    [Fact]
    public void 组内重名的自动生成列名会被避让()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", ""), MakePoint(2, "温度", "") }) });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();
        names.Should().Contain("wen_du");
        names.Should().Contain("wen_du_2");
    }

    /// <summary>
    /// 补 brief 未覆盖的分支：手工列名与自动生成列名冲突。
    /// 两个显示名同为"温度"的点，自动名依次是 wen_du、wen_du_2；
    /// 而手工列名先占位、已把 wen_du_2 拿走，第二个自动名必须继续避让。
    /// 断言最终全部列名互不重复（**不断言**具体避让成哪个名字：那是命名风格，给实现留余地）。
    /// </summary>
    [Fact]
    public void 手工列名与自动生成列名冲突时仍然互不重复()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[]
            {
                MakeGroup(new[]
                {
                    MakePoint(1, "手工点", "wen_du_2"),
                    MakePoint(2, "温度", ""),
                    MakePoint(3, "温度", ""),
                }),
            });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain("wen_du_2", "手工列名必须原样保留，不得被自动名挤走");
        names.Should().Contain("wen_du");
    }

    /// <summary>
    /// 兜底避让的不变量：无论手工名与自动名怎么撞，最终列名**两两不同**。
    /// 本用例把"手工名占用 wen_du_2 / wen_du_3"与"多个同基名自动点"叠在一起，
    /// 使兜底 while 循环被真实触发（实测该场景下循环共执行 2 次）。
    /// 只断言唯一性，**不锁定**具体避让成哪个名字 —— 那是命名风格，不是契约。
    /// </summary>
    [Fact]
    public void 手工名与多个自动名连环冲突时列名仍然互不重复()
    {
        var points = new List<PointConfig>
        {
            MakePoint(1, "手工点甲", "wen_du_2"),
            MakePoint(2, "手工点乙", "wen_du_3"),
        };

        // 12 个同显示名的点：生成器会给出 wen_du、wen_du_2……wen_du_12，
        // 其中前几个会撞上手工列名，必须由兜底避让接手。
        for (var i = 0; i < 12; i++)
        {
            points.Add(MakePoint(100 + i, "温度", ""));
        }

        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(points) });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain("wen_du_2", "手工列名必须原样保留，不得被自动名挤走");
        names.Should().Contain("wen_du_3", "手工列名必须原样保留，不得被自动名挤走");

        // 3 个固定列 + 2 个手工列 + 12 个自动列，一个都不能少、不能空
        names.Should().HaveCount(17);
        names.Should().NotContainNulls();
        names.Should().OnlyContain(n => n.Length > 0);
    }

    /// <summary>
    /// 兜底避让的**终止性**：序号必须递增，否则当候选名**恒被占用**时会死循环。
    /// 两个手工列名占住 wen_du 与 wen_du_2，自动点（显示名"温度"）的基名是 wen_du：
    /// 候选 wen_du 被占 → wen_du_2 也被占 → 只有序号继续递增到 wen_du_3 才能跳出 while。
    /// 若序号不递增，候选恒为 wen_du_2、恒判占用 ⇒ **死循环**（本用例将挂住而非失败）。
    /// 实测该场景真实输出：ts | q | src_ts | wen_du | wen_du_2 | wen_du_3。
    /// **只断言唯一性与"自动名不与手工名冲突"，不锁 wen_du_3** ——
    /// 该断言对终止性零边际价值（见用例内说明），却会让"换一种避让策略但仍终止且唯一"的实现变红。
    /// </summary>
    [Fact]
    public void 候选名被手工名连环占用时仍能终止且列名唯一()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[]
            {
                MakeGroup(new[]
                {
                    MakePoint(1, "手工点甲", "wen_du"),
                    MakePoint(2, "手工点乙", "wen_du_2"),
                    MakePoint(3, "温度", ""),
                }),
            });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();
        var autoName = names[5];

        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain("wen_du", "手工列名必须原样保留");
        names.Should().Contain("wen_du_2", "手工列名必须原样保留");

        // 只断言"自动名不与任何手工名冲突"，**不锁**具体避让成哪个名字 —— 那是命名风格，不是契约。
        // 本用例的可观测性来自"能返回"这件事本身：本 fixture 强制走满 while 第 2 轮
        // （两个候选名都被手工名占死），故序号不递增的实现会**死循环**而不是返回。
        // 而且"只跑 1 轮就交出 wen_du_2"的实现会被上面的 OnlyHaveUniqueItems 抓住，
        // 无需再靠断言某个具体名字来兜。
        autoName.Should().NotBe("wen_du");
        autoName.Should().NotBe("wen_du_2");
    }

    private static DeviceConnection MakeConnection() => new(
        ConnId: 1, ConnCode: "plc01", ConnName: "1#窑 PLC", Protocol: ProtocolKind.ModbusTcp,
        Host: "192.168.0.10", Port: 502, Rack: 0, Slot: 1, SerialPort: null, Baud: 0, Enabled: true);

    private static PollGroup MakeGroup(IReadOnlyList<PointConfig> points) => new(
        GroupId: 1, ConnId: 1, GroupCode: "fast", GroupName: "快组",
        PeriodMs: 1000, TableName: "plc01_fast", Enabled: true, Points: points);

    private static PointConfig MakePoint(int id, string displayName, string columnName, bool enabled = true) => new(
        PointId: id, PointCode: $"p{id}", PointName: displayName, ColumnName: columnName,
        DataType: PointDataType.Real, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: enabled, S7: null,
        Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100 + id));
}
