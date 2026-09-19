using System.Reflection;
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
        // Task 7 给 ModbusAddress 补的寄存器内位偏移（BOOL 点用）。
        // 少了这一行，改 BitOffset 会被热加载判成"配置未变化"而跳过重建。
        new object[] { "BitOffset",       new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100, 3) },
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
    public void 子对象字段清单必须与模型的公开属性完全一致()
    {
        // 防止：给 S7Address/ModbusAddress 加字段后忘记补本测试的清单，
        // 导致该字段只有结构层防护 —— 一旦被写成 || true 就会两层同时失灵（结构层只看文本、
        // 清单里又没有该字段的行为用例，此时没有任何信号）。
        S7Fields().Select(row => (string)row[0])
            .Should().BeEquivalentTo(
                PropertyNamesOf(typeof(S7Address)),
                "S7Fields() 必须覆盖 S7Address 的全部公开属性，新增字段时请同步补充");

        ModbusFields().Select(row => (string)row[0])
            .Should().BeEquivalentTo(
                PropertyNamesOf(typeof(ModbusAddress)),
                "ModbusFields() 必须覆盖 ModbusAddress 的全部公开属性，新增字段时请同步补充");
    }

    [Fact]
    public void 两个子对象都相同且非空时必须判为等价()
    {
        // 正向对照：子对象内容相同必须判等价，否则热加载会永远判定"已变更"
        ConfigComparer.PointEquivalent(MakePoint2(new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)),
                                      MakePoint2(new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)))
            .Should().BeTrue();
    }

    /// <summary>公开实例属性名。口径与 ConfigComparerCoverageTests 一致（静态属性不参与配置比较）。</summary>
    private static IEnumerable<string> PropertyNamesOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name);

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
