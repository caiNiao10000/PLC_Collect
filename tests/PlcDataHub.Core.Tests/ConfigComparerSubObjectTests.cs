using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

/// <summary>
/// 子对象字段的行为级测试。
///
/// 为什么单独有这一层：ConfigComparerBehaviorTests 在 PointConfig 层面把
/// S7 / Modbus 各当作"一个属性"来变异，其"不同值"只差某一个子字段 ——
/// 因此删掉子对象里**其他**字段的比较不会有任何用例失败。
/// 实测：删掉 S7Equivalent 里的 left.BitOffset == right.BitOffset 后，
/// 全部 33 条测试仍然全绿（"以为守住了、其实没守"）。
///
/// 本测试对 S7Address / ModbusAddress 的每个字段逐一构造差异，
/// 经 PointEquivalent 间接触发子对象比较 —— 不改动产品代码可见性。
/// </summary>
public class ConfigComparerSubObjectTests
{
    public static IEnumerable<object[]> S7Fields() => new[]
    {
        new object[] { "Area",       new S7Address(S7Area.Input,     1, 0, 0) },
        new object[] { "Db",         new S7Address(S7Area.DataBlock, 2, 0, 0) },
        new object[] { "ByteOffset", new S7Address(S7Area.DataBlock, 1, 4, 0) },
        new object[] { "BitOffset",  new S7Address(S7Area.DataBlock, 1, 0, 3) },
    };

    public static IEnumerable<object[]> ModbusFields() => new[]
    {
        new object[] { "SlaveId",         new ModbusAddress(2, ModbusRegisterArea.HoldingRegister, 100) },
        new object[] { "Area",            new ModbusAddress(1, ModbusRegisterArea.InputRegister,   100) },
        new object[] { "RegisterAddress", new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 200) },
    };

    [Theory]
    [MemberData(nameof(S7Fields))]
    public void 仅某个_S7_字段不同时必须判为不等价(string fieldName, S7Address different)
    {
        var baseline = MakePoint(new S7Address(S7Area.DataBlock, 1, 0, 0));

        ConfigComparer.PointEquivalent(baseline, baseline with { S7 = different })
            .Should().BeFalse($"S7Address.{fieldName} 的差异必须被 PointEquivalent 检出");
    }

    [Theory]
    [MemberData(nameof(ModbusFields))]
    public void 仅某个_Modbus_字段不同时必须判为不等价(string fieldName, ModbusAddress different)
    {
        var baseline = MakePoint2(new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100));

        ConfigComparer.PointEquivalent(baseline, baseline with { Modbus = different })
            .Should().BeFalse($"ModbusAddress.{fieldName} 的差异必须被 PointEquivalent 检出");
    }

    [Fact]
    public void 两个子对象都相同且非空时必须判为等价()
    {
        // 正向对照：子对象内容相同必须判等价，否则热加载会永远判定"已变更"
        ConfigComparer.PointEquivalent(MakePoint2(new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)),
                                      MakePoint2(new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)))
            .Should().BeTrue();
    }

    /// <summary>S7 非空、Modbus 为 null 的点位。</summary>
    private static PointConfig MakePoint(S7Address s7) => new(
        PointId: 1, PointCode: "p1", PointName: "温度", ColumnName: "wen_du",
        DataType: PointDataType.Bool, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: true, S7: s7, Modbus: null);

    /// <summary>Modbus 非空、S7 为 null 的点位。</summary>
    private static PointConfig MakePoint2(ModbusAddress modbus) => new(
        PointId: 1, PointCode: "p1", PointName: "温度", ColumnName: "wen_du",
        DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: true, S7: null, Modbus: modbus);
}
