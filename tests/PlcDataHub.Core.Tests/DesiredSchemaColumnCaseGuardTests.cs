using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Schema;
using Xunit;

namespace PlcDataHub.Core.Tests;

/// <summary>
/// 列名的**大小写不敏感唯一性**守卫。
/// PostgreSQL 未加引号的标识符大小写不敏感（会折叠成小写），
/// 故 "Wen_Du" 与 "wen_du" 在库里是**同一列** —— 若期望结构里两者并存，
/// 生成的 CREATE TABLE 必报 duplicate column name。
/// 规格 3.5 节只要求"列名统一 ASCII"，并未强制小写，故这种配置是可达的：
/// 显示名"温度"自动生成的 wen_du 是纯小写，而手工列名可以写成 "Wen_Du"。
/// </summary>
public class DesiredSchemaColumnCaseGuardTests
{
    [Fact]
    public void 两个手工列名仅大小写不同时抛异常()
    {
        var act = () => Build(new[]
        {
            MakePoint(1, "点甲", "Wen_Du"),
            MakePoint(2, "点乙", "wen_du"),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Wen_Du*wen_du*");
    }

    [Fact]
    public void 手工列名与自动生成列名仅大小写不同时抛异常()
    {
        // 显示名"温度"自动生成小写 wen_du；手工列名写成 "Wen_Du"。
        // 两者 Ordinal 下不同名，故不被现有 reserved 兜底拦住 —— 正是本守卫要覆盖的来源。
        var act = () => Build(new[]
        {
            MakePoint(1, "手工点", "Wen_Du"),
            MakePoint(2, "温度", ""),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Wen_Du*wen_du*");
    }

    [Fact]
    public void 全小写的正常配置不受守卫影响()
    {
        var schema = Build(new[]
        {
            MakePoint(1, "手工点", "wen_du"),
            MakePoint(2, "温度", ""),
            MakePoint(3, "温度", ""),
        });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain("wen_du");
        names.Should().Contain("wen_du_2");
    }

    private static DesiredSchema Build(IReadOnlyList<PointConfig> points) =>
        DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(points) });

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
