using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class ConfigComparerTests
{
    [Fact]
    public void 固化为事实_record_相等性对集合成员走引用比较()
    {
        // 这条测试故意断言"不相等"，用来把陷阱固化下来 —— 任何依赖
        // PollGroup 默认相等性做配置比对的代码都会在这里得到警告。
        var group1 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });
        var group2 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });

        group1.Should().NotBe(group2, "record 对 IReadOnlyList 成员做引用比较，内容相同也不相等");
    }

    [Fact]
    public void 结构比较认定内容相同的两个组等价()
    {
        var group1 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });
        var group2 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });

        ConfigComparer.GroupsEquivalent(group1, group2).Should().BeTrue();
    }

    [Fact]
    public void 结构比较能发现单个字段的差异而不漏判()
    {
        var group1 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });
        var group2 = MakeGroup(new List<PointConfig> { MakePoint(1, "压力") });

        ConfigComparer.GroupsEquivalent(group1, group2).Should().BeFalse("PointName 不同必须判为不等价");
    }

    [Fact]
    public void 结构比较能发现采集点顺序差异()
    {
        var group1 = MakeGroup(new List<PointConfig> { MakePoint(1, "温度"), MakePoint(2, "压力") });
        var group2 = MakeGroup(new List<PointConfig> { MakePoint(2, "压力"), MakePoint(1, "温度") });

        ConfigComparer.GroupsEquivalent(group1, group2).Should().BeFalse("顺序不同会影响建表列序，必须判为不等价");
    }

    private static PollGroup MakeGroup(IReadOnlyList<PointConfig> points) => new(
        GroupId: 1, ConnId: 1, GroupCode: "fast", GroupName: "快组",
        PeriodMs: 1000, TableName: "plc01_fast", Enabled: true, Points: points);

    private static PointConfig MakePoint(int id, string displayName) => new(
        PointId: id, PointCode: $"p{id}", PointName: displayName, ColumnName: $"col{id}",
        DataType: PointDataType.Real, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: true, S7: null,
        Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100 + id));
}
