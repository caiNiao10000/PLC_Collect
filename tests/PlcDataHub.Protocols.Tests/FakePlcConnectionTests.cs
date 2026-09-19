using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using PlcDataHub.Protocols.Fake;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class FakePlcConnectionTests
{
    [Fact]
    public async Task 脚本指定连接失败时_Connect_抛异常()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: true, FailRead: false, ErrorMessage: "连接被拒绝", DataByPointId: new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);

        var act = async () => await connection.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*连接被拒绝*");
    }

    [Fact]
    public async Task 脚本指定读失败时返回_null_而不抛异常()
    {
        // 规格 5.3 节：单轮采集失败只影响本轮，坏点写 NULL，不能让异常冒出去
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: false, FailRead: false, null, new Dictionary<int, byte[]>()),
            new FakePlcResponse(FailConnect: false, FailRead: true, "读超时", new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().BeNull();
    }

    [Fact]
    public async Task 脚本可以提供真实字节用于端到端解码验证()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x64] }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0);
    }

    [Fact]
    public async Task 响应脚本耗尽后重复返回最后一个响应()
    {
        // 便于写"一直正常"或"一直失败"的长期测试
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x0A] }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        for (var i = 0; i < 5; i++)
        {
            var result = await connection.ReadAsync(points, CancellationToken.None);
            result.Values[points[0]].Should().Be(10.0);
        }
    }

    // ===== 以下为 Task 7 补充：假连接自身的契约（brief 的 4 条用例逐字保留在上面）=====

    [Fact]
    public void 空响应脚本被拒绝()
    {
        var act = () => new FakePlcConnection(new FakePlcScript(Array.Empty<FakePlcResponse>()));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("script")
            .WithMessage("*响应脚本不能为空*");
    }

    [Fact]
    public void null_脚本被拒绝()
    {
        var act = () => new FakePlcConnection(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("script");
    }

    [Fact]
    public async Task 未连接时读取抛异常而不是静默返回坏点()
    {
        // 与真实连接（ModbusTcpConnection）保持同一语义：没连上就不该读。
        // 若这里宽容地返回 null，采集器的"忘记重连"这类缺陷会被假连接掩盖。
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });
        using var connection = new FakePlcConnection(script);

        var act = async () => await connection.ReadAsync(new[] { Point(1) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*尚未连接*");
    }

    [Fact]
    public async Task 连接失败后_IsConnected_为_false()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: true, FailRead: false, ErrorMessage: "拒绝", DataByPointId: new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);

        var act = async () => await connection.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        connection.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAttempts_统计每一次连接尝试含失败()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: true, FailRead: false, "拒绝", new Dictionary<int, byte[]>()),
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x01] }),
        });
        using var connection = new FakePlcConnection(script);

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ConnectAsync(CancellationToken.None));
        connection.ConnectAttempts.Should().Be(1);

        await connection.ConnectAsync(CancellationToken.None);
        connection.ConnectAttempts.Should().Be(2, "失败的尝试也要计数，否则重连逻辑的测试看不出重试发生过");
        connection.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task 读失败会把连接标记为断开()
    {
        // 规格 5.3 节：单轮失败只影响本轮；但"失败"本身就是连接不可用的信号，
        // 采集器的重连逻辑靠 IsConnected 决定是否重连。
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),
            new FakePlcResponse(false, true, "读超时", new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        await connection.ReadAsync(new[] { Point(1) }, CancellationToken.None);

        connection.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task 脚本中没有对应点的字节时该点写坏值()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x64] }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1), Point(2) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values.Should().HaveCount(2, "契约要求每个传入的点都有条目");
        result.Values[points[0]].Should().Be(100.0);
        result.Values[points[1]].Should().BeNull("脚本没给这个点字节，它是本轮的坏点");
    }

    [Fact]
    public async Task Bool_点按_S7_位偏移从脚本字节中取位()
    {
        // 0x08 = 0b0000_1000：位 3 为 1、位 2 为 0（位 0 是最低位，与 DecodeBool 一致）
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>
            {
                [1] = [0x08],
                [2] = [0x08],
            }),
        });
        using var connection = new FakePlcConnection(script, ProtocolKind.S7);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { S7BoolPoint(1, bitOffset: 3), S7BoolPoint(2, bitOffset: 2) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(true, "0x08 的位 3 是 1");
        result.Values[points[1]].Should().Be(false, "0x08 的位 2 是 0");
    }

    [Fact]
    public async Task Bool_点按_Modbus_位偏移从脚本字节中取位()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>
            {
                [1] = [0x00, 0x01],
                [2] = [0x00, 0x01],
            }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { ModbusBoolPoint(1, bitOffset: 0), ModbusBoolPoint(2, bitOffset: 1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(true, "0x0001 的位 0 是 1");
        result.Values[points[1]].Should().Be(false, "0x0001 的位 1 是 0");
    }

    [Fact]
    public async Task 故障序列_断开后重连成功即恢复采集()
    {
        // 本用例正是假连接存在的理由：不用真实设备就能构造
        // "连上 → 正常 → 读超时断开 → 重连 → 恢复" 的完整序列。
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),                       // 0: Connect
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x64] }),  // 1: Read → 100
            new FakePlcResponse(false, true, "读超时", new Dictionary<int, byte[]>()),                    // 2: Read → 坏点
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),                       // 3: Connect（重连）
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0xC8] }),  // 4: Read → 200
        });
        using var connection = new FakePlcConnection(script);
        var points = new[] { Point(1) };

        await connection.ConnectAsync(CancellationToken.None);
        (await connection.ReadAsync(points, CancellationToken.None)).Values[points[0]].Should().Be(100.0);

        (await connection.ReadAsync(points, CancellationToken.None)).Values[points[0]].Should().BeNull();
        connection.IsConnected.Should().BeFalse();

        await connection.ConnectAsync(CancellationToken.None);
        connection.IsConnected.Should().BeTrue();

        (await connection.ReadAsync(points, CancellationToken.None)).Values[points[0]].Should().Be(200.0);
        connection.ConnectAttempts.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_后_IsConnected_为_false()
    {
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });
        var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        connection.Dispose();

        connection.IsConnected.Should().BeFalse();
    }

    // ===== 以下为修复轮 1 补充：假连接必须与真实连接同一套协议语义与校验 =====

    [Fact]
    public async Task 默认扮演_Modbus_设备_纯_S7_点写坏值而不管脚本给了什么()
    {
        // 修复 3(a)：真实连接对 Modbus 为 null 的点写 NULL（它不负责该点）。
        // 修复前假连接照脚本给值 → Plan 2 用假连接验证"按协议分发点集"会**假绿**。
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>
            {
                [1] = [0x00, 0x64], // 脚本"给了"这个 S7 点字节，但假连接扮演的是 Modbus 设备
                [2] = [0x00, 0xC8],
            }),
        });
        using var connection = new FakePlcConnection(script); // 默认 ProtocolKind.ModbusTcp
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { S7BoolPoint(1, 0), Point(2) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().BeNull("纯 S7 点不属于 Modbus 连接，与真实连接一致地写坏值");
        result.Values[points[1]].Should().Be(200.0, "Modbus 点照常取值");
    }

    [Fact]
    public async Task 扮演_S7_设备时_Modbus_点写坏值()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>
            {
                [1] = [0x08],        // S7 位点：0x08 的位 3 是 1
                [2] = [0x00, 0x64],  // Modbus 点：不属于 S7 连接
            }),
        });
        using var connection = new FakePlcConnection(script, ProtocolKind.S7);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { S7BoolPoint(1, 3), Point(2) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(true, "假 S7 连接读 S7 点");
        result.Values[points[1]].Should().BeNull("Modbus 点不属于 S7 连接，与真实连接一致地写坏值");
    }

    [Fact]
    public async Task 脚本可以让读失败但连接保持()
    {
        // 修复 3(b)：真实连接里"从站用异常码拒绝"（SlaveException）的形态是
        // **点是坏值、连接保持、LastError 记为 SlaveRejected**；采集器不该因此重连。
        // 修复前假连接只有"读失败必断开"一种形态，Plan 2 用它验证重连策略会得出与真实设备相反的结论。
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),
            new FakePlcResponse(false, true, "从站异常码 02（非法数据地址）", new Dictionary<int, byte[]>(), KeepConnected: true),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().BeNull();
        connection.IsConnected.Should().BeTrue("坏块但连接保持——这正是 SlaveException 的形态");
        connection.LastError.Should().NotBeNull();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.SlaveRejected);
        connection.LastError.Message.Should().Contain("非法数据地址");
    }

    [Fact]
    public async Task 读失败且不保持连接时_LastError_类别为_Transport()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),
            new FakePlcResponse(false, true, "读超时", new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        await connection.ReadAsync(new[] { Point(1) }, CancellationToken.None);

        connection.IsConnected.Should().BeFalse();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.Transport);
        connection.LastError.Message.Should().Be("读超时");
    }

    [Fact]
    public async Task 连接失败也写入_LastError()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: true, FailRead: false, ErrorMessage: "连接被拒绝", DataByPointId: new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ConnectAsync(CancellationToken.None));

        connection.LastError.Should().NotBeNull();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.Transport);
        connection.LastError.Message.Should().Be("连接被拒绝");
    }

    [Fact]
    public async Task 假连接拒绝非法位偏移_且被拒绝的调用不消耗脚本()
    {
        // 修复 3(引 M3)：假连接必须与真实连接**同样拒绝**非法配置，否则"同一份配置在假连接上能跑"
        // 会让人以为它在真实连接上也能跑。
        // 并且拒绝发生在消耗脚本之前——与真实连接"配置错误不发出任何请求"一致，
        // 否则一次被拒绝的调用会让整条脚本序列错位一格（表现为"值不对"，像解码 bug）。
        // 脚本刻意给三次不同的读响应：这样"消耗了几次"才是可观测的
        // （只有两次时，"多消耗一次"会被脚本耗尽后的'重复返回最后一个'掩盖 → 变异测不出来）。
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()),                       // 0: Connect
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x64] }),  // 1: 100
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0xC8] }),  // 2: 200
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var bad = new[] { ModbusBoolPoint(1, bitOffset: 16) };
        var act = async () => await connection.ReadAsync(bad, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithMessage("*0~15*");

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0,
            "被拒绝的调用没有消耗脚本：这次读到的是脚本第 1 项；若它消耗了，这里会变成第 2 项 200");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(248)]
    public async Task 假连接拒绝非法从站号(int slaveId)
    {
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var point = new PointConfig(
            PointId: 1, PointCode: "p1", PointName: "点1", ColumnName: "p1",
            DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(slaveId, ModbusRegisterArea.HoldingRegister, 100));

        var act = async () => await connection.ReadAsync(new[] { point }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().WithMessage("*从站号*");
    }

    [Fact]
    public async Task 假连接拒绝寄存器区里本期不支持的类型()
    {
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var point = new PointConfig(
            PointId: 7, PointCode: "p7", PointName: "点7", ColumnName: "p7",
            DataType: PointDataType.Dtl, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100));

        var act = async () => await connection.ReadAsync(new[] { point }, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*Dtl*").WithMessage("*p7*");
    }

    [Fact]
    public void 未知协议值被拒绝()
    {
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });

        var act = () => new FakePlcConnection(script, (ProtocolKind)999);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("protocol");
    }

    [Fact]
    public async Task null_点集被拒绝()
    {
        var script = new FakePlcScript(new[] { new FakePlcResponse(false, false, null, new Dictionary<int, byte[]>()) });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var act = async () => await connection.ReadAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("points");
    }

    private static PointConfig Point(int id) => new(
        PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
        DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100));

    private static PointConfig S7BoolPoint(int id, int bitOffset) => new(
        PointId: id, PointCode: $"b{id}", PointName: $"位点{id}", ColumnName: $"b{id}",
        DataType: PointDataType.Bool, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: new S7Address(S7Area.Memory, 0, 0, bitOffset), Modbus: null);

    private static PointConfig ModbusBoolPoint(int id, int bitOffset) => new(
        PointId: id, PointCode: $"mb{id}", PointName: $"Modbus位点{id}", ColumnName: $"mb{id}",
        DataType: PointDataType.Bool, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100, bitOffset));
}
