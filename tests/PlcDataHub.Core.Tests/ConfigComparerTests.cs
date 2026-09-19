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

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void 采集点数量不同时必须判为不等价(int leftCount, int rightCount)
    {
        // 覆盖 PointsEquivalent 的 left.Count != right.Count 分支。增删点位是热加载最常见的变更：
        // 删掉该分支后，left 多的一方会在 right[i] 处越界抛 ArgumentOutOfRangeException
        // （热加载直接崩溃），left 少的一方则会静默返回 true（漏判变更、跳过重建）。
        // 两个方向都必须在场 —— 它们分别对应上面两种不同的失效方式。
        var group1 = MakeGroup(MakePoints(leftCount));
        var group2 = MakeGroup(MakePoints(rightCount));

        ConfigComparer.GroupsEquivalent(group1, group2)
            .Should().BeFalse($"采集点数量不同（{leftCount} vs {rightCount}）必须判为不等价");
    }

    [Fact]
    public void 采集点为_null_时必须抛出_ArgumentNullException()
    {
        // 热加载时"配置尚未加载"传 null 是常见场景，必须得到明确异常而不是 NullReferenceException
        var point = MakePoint(1, "温度");

        Action leftNull = () => ConfigComparer.PointEquivalent(null!, point);
        Action rightNull = () => ConfigComparer.PointEquivalent(point, null!);

        leftNull.Should().Throw<ArgumentNullException>();
        rightNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 采集组为_null_时必须抛出_ArgumentNullException()
    {
        var group = MakeGroup(new List<PointConfig> { MakePoint(1, "温度") });

        Action leftNull = () => ConfigComparer.GroupsEquivalent(null!, group);
        Action rightNull = () => ConfigComparer.GroupsEquivalent(group, null!);

        leftNull.Should().Throw<ArgumentNullException>();
        rightNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 采集点集合为_null_时必须抛出_ArgumentNullException()
    {
        IReadOnlyList<PointConfig> points = new List<PointConfig> { MakePoint(1, "温度") };

        Action leftNull = () => ConfigComparer.PointsEquivalent(null!, points);
        Action rightNull = () => ConfigComparer.PointsEquivalent(points, null!);

        leftNull.Should().Throw<ArgumentNullException>();
        rightNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 两个参数都为_null_时必须抛出_ArgumentNullException()
    {
        // 锁定"null 表示配置尚未加载、必须明确报错"的语义，对三个入口一并成立。
        // 必须单独有这条：上面 6 条断言都只覆盖"恰好一侧为 null"，
        // 把 ThrowIfNull 移到 ReferenceEquals 之后时它们仍会全绿 ——
        // 而 (null, null) 会被 ReferenceEquals 短路成 true（正是"静默跳过重建"最直白的情形）。
        Action pointBothNull = () => ConfigComparer.PointEquivalent(null!, null!);
        Action groupBothNull = () => ConfigComparer.GroupsEquivalent(null!, null!);
        Action pointsBothNull = () => ConfigComparer.PointsEquivalent(null!, null!);

        pointBothNull.Should().Throw<ArgumentNullException>();
        groupBothNull.Should().Throw<ArgumentNullException>();
        pointsBothNull.Should().Throw<ArgumentNullException>();
    }

    private static IReadOnlyList<PointConfig> MakePoints(int count) =>
        Enumerable.Range(1, count).Select(id => MakePoint(id, "温度")).ToList();

    private static PollGroup MakeGroup(IReadOnlyList<PointConfig> points) => new(
        GroupId: 1, ConnId: 1, GroupCode: "fast", GroupName: "快组",
        PeriodMs: 1000, TableName: "plc01_fast", Enabled: true, Points: points);

    private static PointConfig MakePoint(int id, string displayName) => new(
        PointId: id, PointCode: $"p{id}", PointName: displayName, ColumnName: $"col{id}",
        DataType: PointDataType.Real, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: true, S7: null,
        Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100 + id));
}
