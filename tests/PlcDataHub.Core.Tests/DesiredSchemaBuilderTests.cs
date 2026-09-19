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
    /// 类型映射的**覆盖护栏**（与上一条 Theory 配套，两者必须同步）。
    /// <para>
    /// 上一条 Theory 是逐条 <see cref="InlineDataAttribute"/> 列出的，将来给
    /// <see cref="PointDataType"/> 加了枚举成员、却只更新了 <see cref="SqlTypeMapper"/> 或只更新了
    /// Theory 数据时，都会在本用例被拦下：
    /// 枚举成员数与 Theory 条数**必须相等**，任一侧单方面变动都会让等号破裂。
    /// </para>
    /// <para>
    /// 这里刻意**不去反射读 Theory 上的 InlineData 值** —— 那需要调
    /// <c>InlineDataAttribute.GetData(null!)</c>，依赖"xunit 当前版本不校验该参数"这一
    /// **未承诺行为**，将来升级 xunit 会以与业务无关的方式变红。
    /// 改为从 <c>Enum.GetValues</c> 推导期望条数：它遍历的是**每一个**枚举成员，
    /// 是编译期事实，不依赖任何未承诺的运行时行为。
    /// <c>14</c> 不是随手写的常量 —— 它是"枚举成员数"，本用例就是要求 Theory 与它一一对应。
    /// </para>
    /// </summary>
    [Fact]
    public void 类型映射测试覆盖了全部数据类型枚举成员()
    {
        var enumMembers = Enum.GetValues<PointDataType>();

        // Theory 的 [InlineData] 条目数与枚举成员数必须相等（当前各 14 条）
        typeof(DesiredSchemaBuilderTests).GetMethod(nameof(类型映射符合规格_3_4_节))!
            .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
            .Length
            .Should().Be(
                enumMembers.Length,
                "Theory 必须为每个枚举成员各列一条断言；新增枚举成员时请同步补 Theory 数据与映射");

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
    /// 除唯一性外**额外锁死** wen_du_3 —— 这是全任务唯一锁具体列名之处，理由见用例内注释：
    /// 它是"序号递增"的唯一可观测证据，且能返回的实现在此不存在其它可能取值。
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

        // 先只断言"自动名不与任何手工名冲突"（不锁具体避让风格）
        autoName.Should().NotBe("wen_du");
        autoName.Should().NotBe("wen_du_2");

        // 此处是本任务**唯一**锁死具体列名的断言，理由：它是"序号递增"的唯一可观测证据。
        // 走完第 2 轮的唯一方式是序号由 2 递增到 3；若序号不递增，候选恒为 wen_du_2、
        // 恒判占用 ⇒ 死循环（该实现根本无法返回）。因此一个能正常返回的实现在此**只可能**是
        // wen_du_3 —— 两个手工名已占死前两个候选，没有别的命名风格可选。
        names[5].Should().Be("wen_du_3", "序号必须递增才能跳出避让循环，否则死循环");
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
