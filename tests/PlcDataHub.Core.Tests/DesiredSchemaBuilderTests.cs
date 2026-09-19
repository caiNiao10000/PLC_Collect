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
    /// 类型映射的**覆盖护栏**。上一条 Theory 是逐条 <see cref="InlineDataAttribute"/> 列出的，
    /// 将来给 <see cref="PointDataType"/> 加了枚举成员却忘记补映射时，
    /// SqlTypeMapper 抛 ArgumentOutOfRangeException 与否都不会让上一条 Theory 变红 —— 那正是静默漏测。
    /// 本用例把"枚举成员集合"与"Theory 实际断言过的类型集合"直接比对，缺一即失败。
    /// 反射遍历的是测试程序集自身的类型，不读源码文件，故不受文件系统影响。
    /// </summary>
    [Fact]
    public void 类型映射测试覆盖了全部数据类型枚举成员()
    {
        var inlineDataTypes = typeof(DesiredSchemaBuilderTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.Name == nameof(类型映射符合规格_3_4_节))
            .SelectMany(m => m.GetCustomAttributes(typeof(InlineDataAttribute), inherit: false))
            .Cast<InlineDataAttribute>()
            .Select(a => (PointDataType)a.GetData(null!).Single()[0])
            .ToHashSet();

        inlineDataTypes.Should().BeEquivalentTo(
            Enum.GetValues<PointDataType>(),
            "每条映射都必须被断言，新增枚举成员时请同步补 Theory 数据与 SqlTypeMapper 映射");
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
