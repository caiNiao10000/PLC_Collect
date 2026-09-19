using System.Net.Sockets;
using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols.Modbus;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

/// <summary>
/// **真实链路**端到端测试：真的开 socket、真的用 NModbus 客户端，
/// 从站是手写的最小 Modbus TCP 实现（<see cref="FakeModbusTcpSlave"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例回答 <see cref="ModbusTcpConnectionTests"/> 回答不了的两个问题：
/// <list type="number">
///   <item><b>NModbus 3.0.83 在 .NET 8 / net8.0-windows 上是否真的可用</b>
///         （规格 8.1 节列为待验证项）——还原成功不等于运行时可用，这里是真的收发。</item>
///   <item><b>线圈的位序与地址映射</b>：功能码 01 的响应把 8 个位打包进 1 字节。
///         本测试的从站按规范"<b>LSB 对应最低地址</b>"打包，再断言 NModbus 客户端返回的
///         <c>bool[]</c> 满足"索引 i ↔ 地址 start + i"。若 NModbus 按 MSB 优先解释，
///         位 3 的断言会立刻变红（见该用例里的注释）。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>证据强度要如实说明</b>：从站是我按规范手写的，不是真实设备、也不是第三方独立实现。
/// 它证明了"NModbus 客户端的解释与规范文本一致"，<b>不等于</b>证明了"现场那台设备也这么发"。
/// 后者仍需现场用一个已知状态的位比对一次（已写进 <see cref="ModbusTcpConnection"/> 的位路径注释）。
/// </para>
/// </remarks>
public class ModbusTcpWireTests
{
    [Fact]
    public async Task 端到端_读保持寄存器_值与线路上请求响应字节都正确()
    {
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).HoldingRegisters[100] = 0x0064; // 100

        using var connection = await ConnectedAsync(slave);
        var points = new[] { Word(1, 100) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0);

        var request = slave.Requests.Should().ContainSingle().Subject;
        request.UnitId.Should().Be(1);
        request.FunctionCode.Should().Be(3);
        request.StartAddress.Should().Be(100);
        request.Quantity.Should().Be(1);
        request.Pdu.Should().Equal(
            new byte[] { 0x03, 0x00, 0x64, 0x00, 0x01 },
            "功能码 03 + 起始地址 100(0x0064) + 数量 1");

        slave.LastResponsePdu.Should().Equal(
            new byte[] { 0x03, 0x02, 0x00, 0x64 },
            "从站按规范回：功能码 03 + 字节数 2 + 大端寄存器值");
    }

    [Fact]
    public async Task 端到端_线圈位序与地址映射_指数组按_LSB_优先解释()
    {
        using var slave = new FakeModbusTcpSlave();
        var coils = slave.Slave(1).Coils;

        // 地址 100..107 = 1010_0010 反过来写：只有 100、103、107 为 1
        coils[100] = true;
        coils[103] = true;
        coils[107] = true;

        using var connection = await ConnectedAsync(slave);
        var points = Enumerable.Range(0, 8).Select(i => CoilBool(i + 1, 100 + i)).ToArray();

        var result = await connection.ReadAsync(points, CancellationToken.None);

        var expected = new[] { true, false, false, true, false, false, false, true };

        for (var i = 0; i < 8; i++)
        {
            result.Values[points[i]].Should().Be(
                expected[i],
                $"线圈 {100 + i} 必须是请求块里的第 {i} 位（索引 i ↔ 地址 start + i）");
        }

        var request = slave.Requests.Should().ContainSingle().Subject;
        request.FunctionCode.Should().Be(1);
        request.StartAddress.Should().Be(100);
        request.Quantity.Should().Be(8);
        request.Pdu.Should().Equal(new byte[] { 0x01, 0x00, 0x64, 0x00, 0x08 });

        // 从站按规范"LSB 对应最低地址"打包：位 0、3、7 置位 → 0b1000_1001 = 0x89。
        // 若按 MSB 优先打包会是 0x91 —— 本用例的 8 条断言里，地址 103 那条
        // （期望 true、MSB 解释下为 false）就是专门用来区分这两种打包的。
        slave.LastResponsePdu.Should().Equal(
            new byte[] { 0x01, 0x01, 0x89 },
            "功能码 01 + 字节数 1 + 位打包字节（LSB = 最低地址）");
    }

    [Fact]
    public async Task 端到端_离散输入走功能码02()
    {
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).DiscreteInputs[200] = true;
        slave.Slave(1).DiscreteInputs[201] = false;

        using var connection = await ConnectedAsync(slave);
        var points = new[] { CoilBool(1, 200, area: ModbusRegisterArea.DiscreteInput), CoilBool(2, 201, area: ModbusRegisterArea.DiscreteInput) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(true);
        result.Values[points[1]].Should().Be(false);
        slave.Requests.Should().ContainSingle().Which.FunctionCode.Should().Be(2);
        slave.LastResponsePdu![0].Should().Be(2);
    }

    [Fact]
    public async Task 端到端_输入寄存器走功能码04()
    {
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).InputRegisters[300] = 0x00C8; // 200

        using var connection = await ConnectedAsync(slave);
        var points = new[] { Word(1, 300, area: ModbusRegisterArea.InputRegister) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(200.0);
        var request = slave.Requests.Should().ContainSingle().Subject;
        request.FunctionCode.Should().Be(4);
        request.StartAddress.Should().Be(300);
    }

    [Fact]
    public async Task 端到端_同一连接上的两个从站各读各的()
    {
        // C1 的线路级证据：一条 TCP 连接里，从站 1 与从站 2 的寄存器 100 值不同，
        // 若把两个从站合并成一次请求（brief 原始实现的缺陷），其中一个点必然拿到另一个从站的值。
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).HoldingRegisters[100] = 0x0064; // 100
        slave.Slave(2).HoldingRegisters[100] = 0x00C8; // 200

        using var connection = await ConnectedAsync(slave);
        var points = new[] { Word(1, 100, slave: 2), Word(2, 100, slave: 1) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(200.0);
        result.Values[points[1]].Should().Be(100.0);
        slave.Requests.Should().HaveCount(2);
        slave.Requests.Select(r => r.UnitId).Should().Equal(new byte[] { 2, 1 });
        slave.Requests.Should().OnlyContain(r => r.FunctionCode == 3 && r.StartAddress == 100);
    }

    [Fact]
    public async Task 端到端_从站回异常码时该块为坏点但连接保持()
    {
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).HoldingRegisters[100] = 0x0064;
        slave.RespondWithException(2, exceptionCode: 0x02); // 非法数据地址

        using var connection = await ConnectedAsync(slave);
        var points = new[] { Word(1, 100, slave: 1), Word(2, 100, slave: 2) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0, "从站 1 的块不受影响");
        result.Values[points[1]].Should().BeNull();
        connection.IsConnected.Should().BeTrue("从站答复了异常码，链路是好的（NModbus 把它包成 SlaveException）");
    }

    [Fact]
    public async Task 端到端_从站不回包时读超时_该块坏点且连接被标记断开()
    {
        using var slave = new FakeModbusTcpSlave { Mute = true };

        // 超时刻意调小：NModbus 默认会对失败的请求重试（Retries=3、间隔 250ms），
        // 1000ms 的超时会让本用例跑到 4 秒以上。
        var options = new ModbusTcpOptions(TimeoutMs: 200);
        using var connection = new ModbusTcpConnection(Device(slave.Port), options);
        await connection.ConnectAsync(CancellationToken.None);

        var result = await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().BeNull();
        connection.IsConnected.Should().BeFalse("读超时属于传输层故障，必须让采集器去重连");
    }

    [Fact]
    public async Task 端到端_连接未监听的端口时_ConnectAsync_抛异常且不留在已连接状态()
    {
        var slave = new FakeModbusTcpSlave();
        var port = slave.Port;
        slave.Dispose(); // 端口已释放 → 连接会被拒绝

        // 超时给到 5 秒：本机实测（探针）对"已关闭的环回端口"是**真拒绝**（WSAECONNREFUSED），
        // 但拒绝要 ~2072ms 才返回（不是微秒级），500ms 的超时会先触发而把结论变成"超时"。
        using var connection = new ModbusTcpConnection(Device(port), new ModbusTcpOptions(TimeoutMs: 5000));

        var exception = await Record.ExceptionAsync(() => connection.ConnectAsync(CancellationToken.None));

        // 连接失败只有两种合法表现，取决于"netstat 拒绝"还是"被静默丢包"：
        // 被拒绝/不可达 → SocketException；无响应 → 本类包装出的 TimeoutException。
        // 断言"落在允许集合内"而不是"抛了某个异常"，失败消息里带上实际类型，避免假绿。
        exception.Should().NotBeNull("连接未监听的端口必须抛异常，而不是安静地进入已连接状态");
        (exception is SocketException || exception is TimeoutException).Should().BeTrue(
            $"连接失败只应是 {nameof(SocketException)}（被拒绝/不可达）或 {nameof(TimeoutException)}（超时），"
            + $"实际是 {exception!.GetType().Name}：{exception.Message}");

        connection.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task 端到端_两个_BOOL_点共用同一寄存器时各取各位()
    {
        using var slave = new FakeModbusTcpSlave();
        slave.Slave(1).HoldingRegisters[100] = 0x0100; // 只有位 8 是 1

        using var connection = await ConnectedAsync(slave);
        var points = new[] { Bool(1, 100, bitOffset: 0), Bool(2, 100, bitOffset: 8) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(false, "寄存器 0x0100 的位 0 是 0");
        result.Values[points[1]].Should().Be(true, "寄存器 0x0100 的位 8 是 1（高字节的最低位）");
        slave.Requests.Should().ContainSingle("位提取发生在客户端，不会为每个位各发一次请求");
    }

    private static DeviceConnection Device(int port) => new(
        ConnId: 1, ConnCode: "dev1", ConnName: "设备1", Protocol: ProtocolKind.ModbusTcp,
        Host: "127.0.0.1", Port: port, Rack: 0, Slot: 1, SerialPort: null, Baud: 9600, Enabled: true);

    private static async Task<ModbusTcpConnection> ConnectedAsync(FakeModbusTcpSlave slave)
    {
        var connection = new ModbusTcpConnection(Device(slave.Port));
        await connection.ConnectAsync(CancellationToken.None);
        return connection;
    }

    private static PointConfig Word(int id, int register, int slave = 1, ModbusRegisterArea area = ModbusRegisterArea.HoldingRegister) =>
        new(PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
            DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(slave, area, register));

    private static PointConfig Bool(int id, int register, int bitOffset) =>
        new(PointId: id, PointCode: $"b{id}", PointName: $"位点{id}", ColumnName: $"b{id}",
            DataType: PointDataType.Bool, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, register, bitOffset));

    private static PointConfig CoilBool(int id, int register, ModbusRegisterArea area = ModbusRegisterArea.Coil) =>
        new(PointId: id, PointCode: $"c{id}", PointName: $"线圈点{id}", ColumnName: $"c{id}",
            DataType: PointDataType.Bool, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(1, area, register));
}
