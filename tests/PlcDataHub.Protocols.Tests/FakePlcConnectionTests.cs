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
        using var connection = new FakePlcConnection(script);
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
