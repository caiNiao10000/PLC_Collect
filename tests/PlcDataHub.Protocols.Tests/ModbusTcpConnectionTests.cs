using FluentAssertions;
using NModbus;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols.Modbus;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

/// <summary>
/// <see cref="ModbusTcpConnection"/> 的编排、解码与校验测试。
/// 这些用例经**注入的请求通道桩**驱动（<see cref="StubModbusChannel"/>），不依赖网络；
/// 真实链路（NModbus + socket + 线路字节）由 <c>ModbusTcpWireTests</c> 用手写的最小
/// Modbus TCP 从站端到端覆盖。
/// </summary>
public class ModbusTcpConnectionTests
{
    // ================= C1：读块必须按 (从站号, 寄存器区) 分组 =================

    [Fact]
    public async Task 多从站同区被拆成两组_各自用自己的从站号读取()
    {
        // 这是 brief 原始实现最危险的缺陷：只按 Area 分组、整组用第一个点的从站号，
        // 于是一个混了从站 1 与 2 的同区点集全部按从站 1 读 → 从站 2 的点拿到从站 1 的数据，
        // 不抛异常、不返回坏值。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064); // 100
        stub.SetRegisters(2, ModbusRegisterArea.HoldingRegister, 100, 0x00C8); // 200
        using var connection = await ConnectedAsync(stub);

        // 故意把"第一个点"放在从站 2：只按 Area 分组的实现会用从站 2 读全部点。
        var points = new[] { Word(1, 100, slave: 2), Word(2, 100, slave: 1) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(200.0, "从站 2 的寄存器 100 是 0x00C8");
        result.Values[points[1]].Should().Be(100.0, "从站 1 的寄存器 100 是 0x0064");
        stub.Calls.Should().HaveCount(2, "两个从站必须各发一次请求");
        stub.Calls.Select(c => c.SlaveId).Should().Equal(
            new byte[] { 2, 1 },
            "分组按点在入参里的首次出现顺序处理（实测行为）；重点是每个组用**自己的**从站号，而不是第一个点的");
        stub.Calls.Should().OnlyContain(c => c.StartAddress == 100 && c.Quantity == 1);
    }

    [Fact]
    public async Task 同一从站的不同寄存器区分别读取()
    {
        // 保持寄存器 100 与输入寄存器 100 是完全不同的两个寄存器（功能码 03 vs 04），
        // 合并成一次请求会跨越功能码。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064); // 100
        stub.SetRegisters(1, ModbusRegisterArea.InputRegister, 100, 0x00C8);   // 200
        using var connection = await ConnectedAsync(stub);

        var points = new[]
        {
            Word(1, 100, area: ModbusRegisterArea.HoldingRegister),
            Word(2, 100, area: ModbusRegisterArea.InputRegister),
        };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0);
        result.Values[points[1]].Should().Be(200.0);
        stub.Calls.Should().HaveCount(2);
        stub.Calls.Select(c => c.Area).Should().BeEquivalentTo(
            new[] { ModbusRegisterArea.HoldingRegister, ModbusRegisterArea.InputRegister });
    }

    [Fact]
    public async Task 同区多点合并成一次请求_块长按寄存器数而不是字节数()
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x1111, 0x2222, 0x3333, 0x4444, 0x5555, 0x0064);
        using var connection = await ConnectedAsync(stub);

        // 寄存器 100 与 105：空档 = 105 - 101 = 4 < 窗口 16 → 合并成 [100, 106) 共 6 个寄存器
        var points = new[] { Word(1, 100), Word(2, 105) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle("两个点必须合并成一次请求（规格 5.2 节）");
        stub.Calls[0].Should().Be(new ModbusCall(1, ModbusRegisterArea.HoldingRegister, 100, 6));
        result.Values[points[0]].Should().Be(0x1111);
        result.Values[points[1]].Should().Be(100.0,
            "寄存器 105 在块内是第 5 个 → 字节偏移必须乘 2（没乘 2 会读到 0x3333=13107）");
    }

    [Fact]
    public async Task 寄存器地址跨度超出窗口时拆成两个请求()
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 300, 0x00C8);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Word(1, 100), Word(2, 300) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().HaveCount(2);
        stub.Calls[0].Should().Be(new ModbusCall(1, ModbusRegisterArea.HoldingRegister, 100, 1));
        stub.Calls[1].Should().Be(new ModbusCall(1, ModbusRegisterArea.HoldingRegister, 300, 1));
        result.Values[points[0]].Should().Be(100.0);
        result.Values[points[1]].Should().Be(200.0);
    }

    // ================= C2：BOOL 点的两条物理路径 =================

    [Fact]
    public async Task 线圈点走功能码01_合并块里的空洞按地址下标对齐()
    {
        var stub = new StubModbusChannel();
        // 地址 100..103 的四个线圈：只有 103 是 1 → 0b0001
        stub.SetBits(1, ModbusRegisterArea.Coil, 100, false, false, false, true);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { CoilBool(1, 100), CoilBool(2, 103) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle("两个线圈合并成一个位块 [100, 104)");
        stub.Calls[0].Should().Be(new ModbusCall(1, ModbusRegisterArea.Coil, 100, 4));
        result.Values[points[0]].Should().Be(false, "线圈 100 是块内第 0 位");
        result.Values[points[1]].Should().Be(true, "线圈 103 是块内第 3 位——按『点次序』取位会在这里读错");
    }

    [Fact]
    public async Task 离散输入点走功能码02()
    {
        var stub = new StubModbusChannel();
        stub.SetBits(1, ModbusRegisterArea.DiscreteInput, 200, false, true);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { CoilBool(1, 200, area: ModbusRegisterArea.DiscreteInput), CoilBool(2, 201, area: ModbusRegisterArea.DiscreteInput) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle();
        stub.Calls[0].Area.Should().Be(ModbusRegisterArea.DiscreteInput, "离散输入必须走功能码 02（ReadInputs）");
        stub.Calls[0].Quantity.Should().Be(2);
        result.Values[points[0]].Should().Be(false);
        result.Values[points[1]].Should().Be(true);
    }

    [Fact]
    public async Task 位区的单次请求位数也受同一上限约束()
    {
        // 位区每个地址只占一位，于是 MaxRegistersPerRequest 的含义变成"单次最多读多少位"。
        // 功能码 01/02 协议上允许 2000 个线圈，默认 120 对位区是保守的——本用例把这个
        // 行为钉住（它也是 ModbusTcpOptions.MaxRegistersPerRequest 注释里那句承诺的依据）。
        var stub = new StubModbusChannel();
        var bits = Enumerable.Range(0, 200).Select(i => i % 2 == 0).ToArray();
        stub.SetBits(1, ModbusRegisterArea.Coil, 0, bits);
        stub.SetBits(1, ModbusRegisterArea.Coil, 120, bits.Skip(120).ToArray());
        using var connection = await ConnectedAsync(stub);

        var points = Enumerable.Range(0, 200).Select(i => CoilBool(i + 1, i)).ToArray();

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().HaveCount(2, "200 个线圈在 120 位的上限下必须拆成两块");
        stub.Calls[0].Should().Be(new ModbusCall(1, ModbusRegisterArea.Coil, 0, 120));
        stub.Calls[1].Should().Be(new ModbusCall(1, ModbusRegisterArea.Coil, 120, 80));

        for (var i = 0; i < 200; i++)
        {
            result.Values[points[i]].Should().Be(i % 2 == 0, $"线圈 {i} 的位序不能因为分块而错位");
        }
    }

    [Theory]
    // 位 → 寄存器值 的正反两组：正向证明映射对，反向（cross）证明没有把高/低字节搞反。
    // 若位 0 被映射到高字节：第 1 行会变 false、第 5 行会变 true —— 两处都会红。
    [InlineData(0, 0x0001, true)]
    [InlineData(7, 0x0080, true)]
    [InlineData(8, 0x0100, true)]
    [InlineData(15, 0x8000, true)]
    [InlineData(0, 0x8000, false)]
    [InlineData(15, 0x0001, false)]
    [InlineData(7, 0x0100, false)]
    [InlineData(8, 0x0080, false)]
    public async Task 寄存器区_Bool_点取_BitOffset_指定的位(int bitOffset, int registerValue, bool expected)
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, (ushort)registerValue);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Bool(1, 100, bitOffset) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(expected);
        stub.Calls.Should().ContainSingle("位提取不会额外发请求");
    }

    [Theory]
    [InlineData(0x0001, true, false)]
    [InlineData(0x0100, false, true)]
    [InlineData(0x0000, false, false)]
    [InlineData(0x0101, true, true)]
    public async Task 同一个保持寄存器内的不同位各取各的位(int registerValue, bool bit0, bool bit8)
    {
        // C5：两个 BOOL 点共用寄存器 100，BitOffset 分别是 0 与 8。
        // 它们必须落在同一个读块里、各自取到自己的那一位。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, (ushort)registerValue);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Bool(1, 100, bitOffset: 0), Bool(2, 100, bitOffset: 8) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle();
        result.Values[points[0]].Should().Be(bit0);
        result.Values[points[1]].Should().Be(bit8);
    }

    [Fact]
    public async Task 寄存器区的_Bool_点与数值点共存于同一块()
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064, 0x0008);
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Word(1, 100), Bool(2, 101, bitOffset: 3), Bool(3, 101, bitOffset: 0) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle();
        stub.Calls[0].Quantity.Should().Be(2);
        result.Values[points[0]].Should().Be(100.0);
        result.Values[points[1]].Should().Be(true, "寄存器 101 = 0x0008，位 3 是 1");
        result.Values[points[2]].Should().Be(false, "寄存器 101 = 0x0008，位 0 是 0");
    }

    // ================= 逐块故障隔离（规格 5.3 节） =================

    [Fact]
    public async Task 一个从站读失败不影响另一个从站已读到的值()
    {
        // **数据隔离**：失败组在后时，本轮已经从另一组读到的值不会被"整轮作废"。
        // 注意范围：传输故障会把链路标死，**本轮的后续块**不再发请求（见
        // `首个传输失败后_本轮剩余块不再发请求`）；这里保留的是"已读到的数据不丢"。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        stub.FailWith(2, ModbusRegisterArea.HoldingRegister, () => new IOException("连接被重置"));
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Word(1, 100, slave: 1), Word(2, 100, slave: 2) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0, "从站 1 的读块必须不受从站 2 失败的影响");
        result.Values[points[1]].Should().BeNull();
        stub.Calls.Should().HaveCount(2, "两个从站都发过请求（失败组在最后，没有需要跳过的剩余块）");
    }

    [Fact]
    public async Task 传输层故障把连接标记为断开()
    {
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new IOException("连接被重置"));
        using var connection = await ConnectedAsync(stub);

        var result = await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().BeNull();
        connection.IsConnected.Should().BeFalse("链路已不可用，采集器应当重连");
    }

    [Fact]
    public async Task 从站异常码让该块变坏点_但不中止本轮_其余块照常读()
    {
        // 从站"答复了"异常码（例如非法数据地址 0x02）：设备与链路都是好的，坏的是这个块的请求。
        // 若把它也当成传输故障，采集器会每个周期无谓重连一次。
        //
        // **刻意把失败的从站放在第一个**：修复 2 引入的"链路失效即中止本轮"绝不能因此触发——
        // 从站答复了异常码说明链路是好的，后面的块必须照常读。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        stub.FailWith(2, ModbusRegisterArea.HoldingRegister, () => new SlaveException("从站返回异常码 02（非法数据地址）"));
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Word(1, 100, slave: 2), Word(2, 100, slave: 1) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().BeNull("被拒绝的从站其点是坏点");
        result.Values[points[1]].Should().Be(100.0, "链路是好的，后面的块必须照常读（没有被中止）");
        connection.IsConnected.Should().BeTrue("从站答复了异常码，说明链路是好的");
        stub.Calls.Should().HaveCount(2, "两个从站的请求都发出去了");
    }

    [Fact]
    public async Task 配置错误不被吞成坏点_而是在配置期响亮抛出且不发请求()
    {
        // Dtl 本期不支持。**关键在"何时"抛**：必须在任何 I/O 之前（配置期），而不是等解码期。
        // 解码期抛有三个后果：① 异常发生在一次白发的请求之后；② 异常不含点标识
        // （ByteDecoder 拿不到 PointConfig，结构上不可能点名）；③ 异常中断整轮。
        //
        // 行为差异（修复轮 1 明确确认）：含 Dtl 点的组现在**整组在配置期失败**，
        // 而修复前是"请求发出去了、读到一半才整组没数据"。前者更好：错误在任何 I/O 之前、
        // 以点名的方式暴露，而且是**每轮都一样**的确定性失败，不是随机的坏点形态。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0000, 0x0000, 0x0000, 0x0000, 0x0064);
        using var connection = await ConnectedAsync(stub);

        var points = new[]
        {
            new PointConfig(
                PointId: 1, PointCode: "dtl1", PointName: "时间点", ColumnName: "dtl1",
                DataType: PointDataType.Dtl, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
                S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100)),
            Word(2, 104),
        };

        var act = async () => await connection.ReadAsync(points, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Dtl*")
            .WithMessage("*dtl1*")
            .WithMessage("*本期不支持*");
        stub.Calls.Should().BeEmpty("配置错误必须在发出任何请求之前抛出（否则白花一次请求）");
        connection.IsConnected.Should().BeTrue("类型不支持是配置错误，不是链路故障");
        connection.LastError.Should().BeNull("配置错误靠抛异常暴露，异常本身就是信号（不写 LastError）");
    }

    [Theory]
    [InlineData(PointDataType.Dtl)]
    [InlineData(PointDataType.String)]
    public async Task 寄存器区的不支持类型点在配置期被拒绝(PointDataType type)
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Typed(7, type, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage($"*{type}*")
            .WithMessage("*p7*")
            .WithMessage("*本期不支持*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 读通道抛出_NotSupportedException_时不被吞成坏点()
    {
        // 这是"逐块故障隔离绝不吞配置错误"这条性质的**机制守卫**。
        // 配置期检查（上面两条用例）已经把 Dtl/String 挡在 I/O 之前，所以正常路径再也走不到
        // 块级 catch 的 NotSupportedException 分支；但那个分支是第二道防线，必须继续有效。
        // 这里直接从通道注入同类型异常，验证它冒泡而不是变成 NULL。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new NotSupportedException("模拟：库里抛出的不支持类型"));
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*模拟*");
        connection.IsConnected.Should().BeTrue("把它当成链路故障会让采集器做无谓重连");
    }

    [Fact]
    public async Task 白名单之外的异常既冒泡也把连接标记为失效()
    {
        // 两条性质一起钉住：
        // ① **必须冒泡**——catch-all 会把库/代码缺陷（例如 NModbus 的内部状态错误）伪装成
        //    "本块偶发读失败"，让缺陷永远只表现为"某些点没数据"（与 C2 同类失效）；
        // ② **必须把连接标记为失效**——能从这个 socket 上抛出的未知异常说明链路状态已不可信。
        //    修复轮 2 之前这里只冒泡、不标失效，于是下一轮会放行、继续在同一条可疑 socket 上读
        //    ——"标死却还在用"的同型矛盾换了个触发条件（实测：设备消失/对端 RST 后
        //    NModbus 从第二次调用起恒抛 InvalidOperationException）。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new InvalidOperationException("模拟：库内部状态错误"));
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*库内部状态错误*");
        connection.IsConnected.Should().BeFalse("未知异常同样说明链路不可信，必须让采集器重连");
        connection.LastError.Should().NotBeNull();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.Unexpected);
        connection.LastError.Message.Should().Contain("InvalidOperationException").And.Contain("库内部状态错误");
    }

    [Fact]
    public async Task 未知异常之后_下一轮读取抛尚未连接而不复用可疑连接()
    {
        // 上一条用例的"后果"：未知异常标失效之后，绝不允许再在同一连接上读
        // （这正是"每轮都抛、永不重连、采集彻底停摆"那条失效路径的回归网）。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new InvalidOperationException("模拟：库内部状态错误"));
        using var connection = await ConnectedAsync(stub);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None));

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*尚未连接*");
        stub.Calls.Should().ContainSingle("标失效之后一个请求都不许再发");
    }

    // ================= 修复 2：首个传输失败后本轮不再发请求 =================

    [Fact]
    public async Task 首个传输失败后_本轮剩余块不再发请求()
    {
        // 两个块（同一从站同一区、地址相距很远 → 必然拆成两块）。
        // 修复前：两块各自等满超时（NModbus 默认 3 次重试 + 250ms 间隔 ≈ 单块 4.75s，十块能拖到几十秒）。
        // 修复后：第一个块失败即判定链路失效，第二个块直接标坏点、不发请求。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new IOException("模拟：连接被重置"));
        using var connection = await ConnectedAsync(stub);

        var points = new[] { Word(1, 100), Word(2, 3000) };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        stub.Calls.Should().ContainSingle("链路失效后不得再发请求（否则每个块都要各自等满超时）");
        result.Values[points[0]].Should().BeNull();
        result.Values[points[1]].Should().BeNull("未发请求的块照样是坏点，条目不能少");
        result.Values.Should().HaveCount(2);
        connection.IsConnected.Should().BeFalse();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.Transport);
    }

    [Fact]
    public async Task 传输失败后_下一轮读取直接抛异常而不复用可疑连接()
    {
        // IsConnected 的恢复语义：**只有 ConnectAsync 能置回 true**。
        // 理由：读超时后从站可能仍把那次响应发回来，复用同一 socket 会让下一个请求捡到迟到帧
        // （错值且不报错）。故传输失败后必须重新建连，而不是"继续读读看"。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new IOException("模拟：连接被重置"));
        using var connection = await ConnectedAsync(stub);

        await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);
        connection.IsConnected.Should().BeFalse();

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*尚未连接*");
        stub.Calls.Should().ContainSingle("第二轮一个请求都不该发出去");
    }

    // ================= 修复 4：LastError（让"点一直坏值"可归因） =================

    [Fact]
    public async Task 从站异常码写入_LastError_而连接保持()
    {
        // 异常码 01（不支持该功能码）/ 02（地址超设备范围）恰是现场最常见的**配置**错误形态。
        // 它们被降级为坏点是对的（链路是好的，不该重连），但**必须留痕**，
        // 否则"某点每周期静默 NULL"与"偶发读失败"完全不可区分。
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new SlaveException("从站返回异常码 02（非法数据地址）"));
        using var connection = await ConnectedAsync(stub);

        connection.LastError.Should().BeNull("还没读过，不该有失败记录");

        var result = await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().BeNull();
        connection.IsConnected.Should().BeTrue("从站答复了就说明链路是好的");

        connection.LastError.Should().NotBeNull();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.SlaveRejected);
        connection.LastError.Message.Should()
            .Contain("HoldingRegister")
            .And.Contain("100")
            .And.Contain("请检查", "消息要能回答'该怎么办'——这类失败该改配置，不是重连");
    }

    [Fact]
    public async Task 传输故障写入_LastError_类别为_Transport()
    {
        var stub = new StubModbusChannel();
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new TimeoutException("模拟：读超时"));
        using var connection = await ConnectedAsync(stub);

        await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        connection.LastError.Should().NotBeNull();
        connection.LastError!.Kind.Should().Be(ConnectionFailureKind.Transport);
        connection.LastError.Message.Should().Contain("TimeoutException").And.Contain("读超时");
    }

    [Fact]
    public async Task 成功的一轮不清空_LastError()
    {
        // "最近一次失败"是**粘性**语义：清空会让"上周期出过错"在运行状态里消失，
        // 而操作员要看的正是"有没有出过错、错在哪一类"。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        stub.FailWith(1, ModbusRegisterArea.HoldingRegister, () => new SlaveException("拒绝"));
        using var connection = await ConnectedAsync(stub);

        await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);
        var first = connection.LastError;
        first.Should().NotBeNull();

        stub.ClearFailures();
        var result = await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().Be(100.0);
        connection.LastError.Should().BeSameAs(first, "成功不清空 LastError（粘性语义）");
    }

    // ================= C4：从站号边界与地址校验 =================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(248)]
    [InlineData(300)]
    public async Task 从站号越界被拒绝且不发任何请求(int slaveId)
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Word(7, 100, slave: slaveId) }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*从站号*")
            .WithMessage("*1~247*")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty("配置错误必须在发出任何请求之前就被拦下");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(247)]
    public async Task 从站号边界_1_与_247_是合法的(byte slaveId)
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(slaveId, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        using var connection = await ConnectedAsync(stub);

        var result = await connection.ReadAsync(new[] { Word(1, 100, slave: slaveId) }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().Be(100.0);
        stub.Calls.Should().ContainSingle().Which.SlaveId.Should().Be(slaveId);
    }

    [Fact]
    public async Task 位区里的非_Bool_点被拒绝()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(
            new[] { Word(7, 100, area: ModbusRegisterArea.Coil) }, CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*位区*")
            .WithMessage("*p7*")
            .WithMessage("*Word*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 位区里的_BitOffset_非零被拒绝()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { CoilBool(7, 100, bitOffset: 3) }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*BitOffset 必须为 0*")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task 寄存器区_Bool_的位偏移越界被拒绝(int bitOffset)
    {
        // 越界的位偏移取不出任何位（DecodeBool 返回 null）→ 该点会每周期写坏值且没有信号。
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Bool(7, 100, bitOffset) }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*0~15*")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 负地址被拒绝()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(new[] { Word(7, -1) }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 末寄存器越过地址上限被拒绝()
    {
        // DWORD 占 2 个寄存器：65535 处的 DWORD 会覆盖 65535 与 65536，后者回绕。
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(
            new[]
            {
                new PointConfig(
                    PointId: 7, PointCode: "p7", PointName: "点7", ColumnName: "p7",
                    DataType: PointDataType.DWord, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
                    S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 65535)),
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*65536*")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 末寄存器恰好等于地址上限是合法的()
    {
        // 边界另一侧：65534 处的 DWORD 覆盖 65534/65535，合法且必须能读。
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 65534, 0x0000, 0x0064);
        using var connection = await ConnectedAsync(stub);

        var point = new PointConfig(
            PointId: 7, PointCode: "p7", PointName: "点7", ColumnName: "p7",
            DataType: PointDataType.DWord, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 65534));

        var result = await connection.ReadAsync(new[] { point }, CancellationToken.None);

        result.Values[point].Should().Be(100.0);
        stub.Calls.Should().ContainSingle().Which.StartAddress.Should().Be(65534);
    }

    [Fact]
    public async Task 未定义的寄存器区被拒绝()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(
            new[] { Word(7, 100, area: (ModbusRegisterArea)99) }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("points")
            .WithMessage("*不是已定义的值*")
            .WithMessage("*p7*");
        stub.Calls.Should().BeEmpty();
    }

    // ================= 连接生命周期与条目完整性 =================

    [Fact]
    public async Task 未连接时读取抛异常()
    {
        var stub = new StubModbusChannel();
        using var connection = new ModbusTcpConnection(Device(), null, stub);

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*尚未连接*");
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Dispose_后不可读取且通道被释放()
    {
        var stub = new StubModbusChannel();
        var connection = await ConnectedAsync(stub);

        connection.Dispose();

        connection.IsConnected.Should().BeFalse();
        stub.DisposeCount.Should().Be(1);

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*尚未连接*");
    }

    [Fact]
    public async Task 已取消的令牌让读取立刻抛出()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await connection.ReadAsync(new[] { Word(1, 100) }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 非_Modbus_点写坏值且不发请求()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var s7Point = new PointConfig(
            PointId: 9, PointCode: "s9", PointName: "纯S7点", ColumnName: "s9",
            DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
            S7: new S7Address(S7Area.DataBlock, 1, 0, 0), Modbus: null);

        var result = await connection.ReadAsync(new[] { s7Point }, CancellationToken.None);

        result.Values.Should().ContainSingle().Which.Value.Should().BeNull();
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 每个传入的点都有条目_混合点集也不例外()
    {
        var stub = new StubModbusChannel();
        stub.SetRegisters(1, ModbusRegisterArea.HoldingRegister, 100, 0x0064);
        stub.SetBits(1, ModbusRegisterArea.Coil, 10, true);
        using var connection = await ConnectedAsync(stub);

        var points = new[]
        {
            Word(1, 100),
            CoilBool(2, 10),
            new PointConfig(
                PointId: 3, PointCode: "s3", PointName: "纯S7点", ColumnName: "s3",
                DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
                S7: new S7Address(S7Area.DataBlock, 1, 0, 0), Modbus: null),
        };

        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values.Should().HaveCount(3);
        result.Values[points[0]].Should().Be(100.0);
        result.Values[points[1]].Should().Be(true);
        result.Values[points[2]].Should().BeNull();
    }

    [Fact]
    public async Task 空点集返回空结果且不发请求()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var result = await connection.ReadAsync(Array.Empty<PointConfig>(), CancellationToken.None);

        result.Values.Should().BeEmpty();
        stub.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task null_点集被拒绝()
    {
        var stub = new StubModbusChannel();
        using var connection = await ConnectedAsync(stub);

        var act = async () => await connection.ReadAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("points");
    }

    // ================= 构造期校验 =================

    [Fact]
    public void 协议不是_ModbusTcp_时构造失败()
    {
        var act = () => new ModbusTcpConnection(Device(protocol: ProtocolKind.ModbusRtu));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("connection")
            .WithMessage("*ModbusTcp*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Host_为空时构造失败(string? host)
    {
        var act = () => new ModbusTcpConnection(Device(host: host));

        act.Should().Throw<ArgumentException>()
            .WithParameterName("connection")
            .WithMessage("*Host*");
    }

    [Fact]
    public void null_连接配置被拒绝()
    {
        var act = () => new ModbusTcpConnection(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("connection");
    }

    [Theory]
    [InlineData(0, 120, 1000, "MergeWindowRegisters")]
    [InlineData(16, 0, 1000, "MaxRegistersPerRequest")]
    [InlineData(16, 126, 1000, "MaxRegistersPerRequest")]
    [InlineData(16, 120, 0, "TimeoutMs")]
    [InlineData(16, 120, -5, "TimeoutMs")]
    public void 选项越界时构造失败(int window, int limit, int timeoutMs, string expectedField)
    {
        // 这三类都必须在构造期就响亮失败：
        //   · 上限 >125 会发出协议不允许的请求，采集期只表现为"这个块一直失败"；
        //   · 超时 0 在 .NET socket 语义里是无限等待 → 采集线程永久挂住、永不重连。
        var options = new ModbusTcpOptions(window, limit, timeoutMs);

        var act = () => new ModbusTcpConnection(Device(), options);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("options")
            .WithMessage($"*{expectedField}*");
    }

    [Fact]
    public void 单请求上限_125_是协议上界且被接受()
    {
        var options = new ModbusTcpOptions(MaxRegistersPerRequest: ModbusTcpConnection.MaxRegistersPerModbusRequest);

        var act = () => new ModbusTcpConnection(Device(), options);

        act.Should().NotThrow("125 恰好是功能码 03/04 允许的最大寄存器数，边界必须可用");
    }

    // ================= fixtures =================

    private static DeviceConnection Device(
        ProtocolKind protocol = ProtocolKind.ModbusTcp,
        string? host = "127.0.0.1",
        int port = 502) => new(
            ConnId: 1, ConnCode: "dev1", ConnName: "设备1", Protocol: protocol, Host: host, Port: port,
            Rack: 0, Slot: 1, SerialPort: null, Baud: 9600, Enabled: true);

    private static async Task<ModbusTcpConnection> ConnectedAsync(StubModbusChannel stub, ModbusTcpOptions? options = null)
    {
        var connection = new ModbusTcpConnection(Device(), options, stub);
        await connection.ConnectAsync(CancellationToken.None);
        return connection;
    }

    private static PointConfig Word(int id, int register, int slave = 1, ModbusRegisterArea area = ModbusRegisterArea.HoldingRegister) =>
        Point(id, PointDataType.Word, slave, area, register, 0);

    private static PointConfig CoilBool(int id, int register, int bitOffset = 0, ModbusRegisterArea area = ModbusRegisterArea.Coil) =>
        Point(id, PointDataType.Bool, 1, area, register, bitOffset);

    private static PointConfig Bool(int id, int register, int bitOffset, int slave = 1) =>
        Point(id, PointDataType.Bool, slave, ModbusRegisterArea.HoldingRegister, register, bitOffset);

    /// <summary>指定数据类型的寄存器区点（用于 Dtl / String 这类"本期不支持"的用例）。</summary>
    private static PointConfig Typed(int id, PointDataType type, int register) =>
        Point(id, type, 1, ModbusRegisterArea.HoldingRegister, register, 0);

    private static PointConfig Point(int id, PointDataType type, int slave, ModbusRegisterArea area, int register, int bitOffset) => new(
        PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
        DataType: type, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(slave, area, register, bitOffset));
}
