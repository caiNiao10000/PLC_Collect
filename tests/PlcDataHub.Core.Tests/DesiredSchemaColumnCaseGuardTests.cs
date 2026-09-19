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

    /// <summary>
    /// 兜底避让是**裸拼接**（<c>$"{基名}_{序号}"</c>），不经 <c>ColumnNameGenerator</c> 的字节预算，
    /// 故基名恰为 63 字节时避让会产出 65 字节列名。
    /// PostgreSQL 对超长标识符是**静默截断**：截断回 63 字节后与触发避让的那个手工列同名，
    /// 建表报 duplicate column name。大小写守卫看不见它（它只查大小写），必须由字节守卫拦住。
    /// </summary>
    [Fact]
    public void 列名超过_63_字节时抛异常而不是静默超限()
    {
        var longName = new string('a', 63);

        var act = () => Build(new[]
        {
            MakePoint(1, "手工点", longName),
            MakePoint(2, longName, ""),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*65*63*");
    }

    /// <summary>
    /// 边界对照：恰好 63 字节是合法上限，不得被守卫误杀。
    /// 若把检查写成 <c>&gt;=</c>，本用例变红。
    /// </summary>
    [Fact]
    public void 恰好_63_字节的列名不被误杀()
    {
        var exactName = new string('a', 63);

        var schema = Build(new[] { MakePoint(1, "手工点", exactName) });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();

        names.Should().Contain(exactName);
        names.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// 措辞：**完全相同**的重复列名不该被说成"仅大小写不同"——那是最可能收到的真实场景，
    /// 误导性诊断会把用户引到错误方向。
    /// </summary>
    [Fact]
    public void 完全相同的重复列名给出准确措辞()
    {
        var act = () => Build(new[]
        {
            MakePoint(1, "点甲", "wen_du"),
            MakePoint(2, "点乙", "wen_du"),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*存在重复列名*")
            .WithMessage("*wen_du*");
    }

    /// <summary>
    /// 守卫放在 <c>BuildTable</c> 末尾，对**全部列**（含 3 个固定列）生效，而不只是数据列 ——
    /// 故手工列名写 <c>TS</c> 会撞上固定列 <c>ts</c>：两者在 PG 里是同一列，建表必失败。
    /// 本用例锁定代码注释里"顺带覆盖固定列"这一声称（此前只是声称、无测试）。
    /// </summary>
    [Fact]
    public void 手工列名与固定列仅大小写不同时抛异常()
    {
        var act = () => Build(new[] { MakePoint(1, "手工点", "TS") });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ts*TS*");
    }

    /// <summary>
    /// 自动生成列名（由显示名 <c>ts</c> 生成 <c>ts</c>）撞上固定列 <c>ts</c> 时，
    /// 走的是"**完全相同**的重复"分支，故消息用的是"存在重复列名"措辞、
    /// 且提示里同时涵盖"手工列名 / 显示名写成固定列名"这一来源 ——
    /// 该分支不止手工列名能触发，显示名恰好是 <c>ts</c>/<c>q</c>/<c>src_ts</c> 同样能触发。
    /// </summary>
    [Fact]
    public void 显示名恰好等于固定列名时给出准确措辞()
    {
        var act = () => Build(new[] { MakePoint(1, "ts", "") });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*存在重复列名*")
            .WithMessage("*固定列名*");
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
