using System.Reflection;
using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

/// <summary>
/// ConfigComparer 的**行为级**测试：对每个属性构造"仅该属性不同"的两个对象，
/// 断言被判定为不等价。
///
/// 为什么需要它：ConfigComparerCoverageTests 是**文本级**检查，它只能证明属性名
/// 出现在比较方法体里，不能证明该表达式真参与结果 ——
/// 把某行改成 <c>&amp;&amp; (left.X == right.X || true)</c> 依然骗得过它。
/// 本测试从行为上堵死这个盲区，两层互补：
///   结构层（反射）保证"属性被同步到比较器"，行为层（本测试）保证"比较真的生效"。
///
/// 维护方式：新增属性时若本测试的映射表里没有对应不同值，会以
/// 明确的失败信息提示补齐（见 Unhandled）。
/// </summary>
public class ConfigComparerBehaviorTests
{
    /// <summary>每个属性对应的"不同值"。新增属性时在此登记。</summary>
    private static readonly Dictionary<string, object> DifferentValues = new()
    {
        ["PointId"] = 2,
        ["PointCode"] = "p2",
        ["PointName"] = "压力",
        ["ColumnName"] = "ya_li",
        ["DataType"] = PointDataType.Word,
        ["ByteOrder"] = ByteOrder.Little,
        ["Scale"] = 2.0,
        ["Offset"] = 5.0,
        ["Enabled"] = false,
        ["S7"] = new S7Address(S7Area.DataBlock, 1, 4, 0),
        ["Modbus"] = new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 200),
        ["GroupId"] = 2,
        ["ConnId"] = 2,
        ["GroupCode"] = "slow",
        ["GroupName"] = "慢组",
        ["PeriodMs"] = 5000,
        ["TableName"] = "plc01_slow",
        // Points 是集合：标量表达不了"不同值"，MutateGroup 里单独构造（换成一个不同的点列表）。
        // 这里登记的不是值而是"已登记"这个事实 —— 缺了它，"新增属性必须登记"的失败通道对本属性失效。
        ["Points"] = "(集合类型，见 MutateGroup)",
    };

    public static IEnumerable<object[]> PointConfigProperties() =>
        MutatableProperties(typeof(PointConfig)).Select(p => new object[] { p });

    public static IEnumerable<object[]> PollGroupProperties() =>
        MutatableProperties(typeof(PollGroup)).Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(PointConfigProperties))]
    public void 仅某个采集点属性不同时必须判为不等价(string propertyName)
    {
        var baseline = MakePoint();
        var mutated = MutatePoint(baseline, propertyName);

        ConfigComparer.PointEquivalent(baseline, mutated)
            .Should().BeFalse($"属性 {propertyName} 的差异必须被 PointEquivalent 检出");
    }

    [Theory]
    [MemberData(nameof(PollGroupProperties))]
    public void 仅某个采集组属性不同时必须判为不等价(string propertyName)
    {
        var baseline = MakeGroup();
        var mutated = MutateGroup(baseline, propertyName);

        ConfigComparer.GroupsEquivalent(baseline, mutated)
            .Should().BeFalse($"属性 {propertyName} 的差异必须被 GroupsEquivalent 检出");
    }

    [Fact]
    public void 内容完全相同但实例不同的两个组必须判为等价()
    {
        // 这条是正向对照：结构比较必须对相同内容返回 true，
        // 否则热加载会永远判定"已变更"（正是 record 默认相等性的那个坑）
        ConfigComparer.GroupsEquivalent(MakeGroup(), MakeGroup()).Should().BeTrue();
    }

    // ---- 基础设施 ----

    private static PointConfig MakePoint() => new(
        PointId: 1,
        PointCode: "p1",
        PointName: "温度",
        ColumnName: "wen_du",
        DataType: PointDataType.Real,
        ByteOrder: ByteOrder.Big,
        Scale: 1.0,
        Offset: 0.0,
        Enabled: true,
        S7: new S7Address(S7Area.DataBlock, 1, 0, 0),
        Modbus: null);

    private static PollGroup MakeGroup() => new(
        GroupId: 1,
        ConnId: 1,
        GroupCode: "fast",
        GroupName: "快组",
        PeriodMs: 1000,
        TableName: "plc01_fast",
        Enabled: true,
        Points: new List<PointConfig> { MakePoint() });

    private static PointConfig MutatePoint(PointConfig point, string propertyName)
    {
        var different = ResolveDifferentValue(propertyName);
        return propertyName switch
        {
            "PointId" => point with { PointId = (int)different },
            "PointCode" => point with { PointCode = (string)different },
            "PointName" => point with { PointName = (string)different },
            "ColumnName" => point with { ColumnName = (string)different },
            "DataType" => point with { DataType = (PointDataType)different },
            "ByteOrder" => point with { ByteOrder = (ByteOrder)different },
            "Scale" => point with { Scale = (double)different },
            "Offset" => point with { Offset = (double)different },
            "Enabled" => point with { Enabled = (bool)different },
            "S7" => point with { S7 = (S7Address)different },
            "Modbus" => point with { Modbus = (ModbusAddress)different },
            _ => throw Unhandled(propertyName, nameof(MutatePoint)),
        };
    }

    private static PollGroup MutateGroup(PollGroup group, string propertyName)
    {
        var different = ResolveDifferentValue(propertyName);
        return propertyName switch
        {
            "GroupId" => group with { GroupId = (int)different },
            "ConnId" => group with { ConnId = (int)different },
            "GroupCode" => group with { GroupCode = (string)different },
            "GroupName" => group with { GroupName = (string)different },
            "PeriodMs" => group with { PeriodMs = (int)different },
            "TableName" => group with { TableName = (string)different },
            "Enabled" => group with { Enabled = (bool)different },
            "Points" => group with { Points = new List<PointConfig> { MakePoint() with { PointId = 99, PointCode = "p99" } } },
            _ => throw Unhandled(propertyName, nameof(MutateGroup)),
        };
    }

    private static object ResolveDifferentValue(string propertyName) =>
        DifferentValues.TryGetValue(propertyName, out var value)
            ? value
            : throw Unhandled(propertyName, nameof(DifferentValues));

    private static Exception Unhandled(string propertyName, string where) =>
        new InvalidOperationException(
            $"模型新增属性 {propertyName} 未在 {where} 中登记不同值。"
            + "请补上映射，否则该属性会失去行为级防护。");

    private static IEnumerable<string> MutatableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal);
}
