using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class ReadBlockPlannerTests
{
    [Fact]
    public void 连续地址合并成一次请求()
    {
        var points = new[]
        {
            Point(1, 100), Point(2, 101), Point(3, 102), Point(4, 103),
        };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].StartAddress.Should().Be(100);
        blocks[0].Length.Should().Be(4);
        blocks[0].Points.Should().HaveCount(4);
    }

    [Fact]
    public void 地址间隔在窗口内时合并并留下空洞()
    {
        var points = new[] { Point(1, 100), Point(2, 105) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].StartAddress.Should().Be(100);
        blocks[0].Length.Should().Be(6, "要把空洞一起读回来，换取更少的请求次数");
    }

    [Fact]
    public void 地址间隔超出窗口时拆成两个请求()
    {
        var points = new[] { Point(1, 100), Point(2, 200) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().HaveCount(2);
        blocks[0].StartAddress.Should().Be(100);
        blocks[1].StartAddress.Should().Be(200);
    }

    [Fact]
    public void 单个请求长度不超过上限()
    {
        // 300 个连续寄存器，上限 120，必须切成多块
        var points = Enumerable.Range(0, 300).Select(i => Point(i + 1, 1000 + i)).ToArray();

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().NotBeEmpty();
        blocks.Should().OnlyContain(b => b.Length <= 120);
        blocks.Sum(b => b.Points.Count).Should().Be(300, "不能漏点也不能重复");
    }

    [Fact]
    public void 不同采集点类型占用不同寄存器数()
    {
        // REAL 占 2 个寄存器，所以 100 与 102 是连续的
        var real = Point(1, 100, PointDataType.Real);
        var next = Point(2, 102, PointDataType.Word);

        var blocks = ReadBlockPlanner.PlanForModbus(new[] { real, next }, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].Length.Should().Be(3, "REAL 占 2 个寄存器 + WORD 占 1 个");
    }

    [Fact]
    public void 空输入返回空计划()
    {
        ReadBlockPlanner.PlanForModbus(Array.Empty<PointConfig>(), 16, 120).Should().BeEmpty();
    }

    [Fact]
    public void 地址间隔等于窗口时按窗口边界拆分()
    {
        // ⚠️ 本条与下一条的期望是**实测得出**的，不是从参数名"mergeWindowRegisters"推理出来的。
        // 实测边界（合并窗口 = 16）：
        //   间隔 0..15 → 合并（1 个块）；间隔 16 → 拆分（2 个块，间隔 17 亦然）。
        // 成因：`pointStart - blockEnd < mergeWindowRegisters` 是严格小于，
        // 而 blockEnd 是"上个点之后第一个未被占用的寄存器"，故可容忍的间隔恰为 0..窗口-1。
        // 即参数名里的"窗口 16"实际含义是"间隔不超过 15"，边界比名字暗示的窄一格。
        // 本项目第一个版本的这条用例就凭推理写成了"间隔 16 必须拆"，实测红了才改对。
        var points = new[] { Point(1, 100), Point(2, 117) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().HaveCount(2, "间隔 17 超出窗口可容忍的 0..15，必须拆分");
        blocks[0].StartAddress.Should().Be(100);
        blocks[1].StartAddress.Should().Be(117);
    }

    [Fact]
    public void 地址间隔落在窗口内最后一个可用值时仍然合并()
    {
        // 与上一条配对，卡住严格小于的边界：间隔 15 是窗口 16 能容忍的最大间隔 → 合并。
        // 两条一起把 gap ∈ {15, 17} 钉在两侧，使"把 < 改成 <=（或给 gap 加减 1）"这类
        // 改动必然可观测。原实现里 gap±1 的变异在只有"间隔 5/100"的用例下是完全不可观测的。
        var points = new[] { Point(1, 100), Point(2, 115) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle("间隔 15 是窗口 16 能容忍的最大间隔（0..15）");
        blocks[0].Length.Should().Be(16);
    }

    [Fact]
    public void 两点相隔窗口数个寄存器时仍然合并()
    {
        // 把实测边界本身固化下来：寄存器 100 与 116（相隔 16 个寄存器、中间空洞 15 个）
        // 在窗口 16 下**合并**成一个长度 17 的块。
        // 参数名的字面含义会让人以为"间隔 16 ≥ 窗口 16 应拆分"，故必须有测试把真实语义锁死，
        // 否则后人"修 bug"时会把这条边界改反且不自知。
        var points = new[] { Point(1, 100), Point(2, 116) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle("实测：可容忍的间隔是 0..窗口-1，此例间隔为 15");
        blocks[0].Length.Should().Be(17);
    }

    [Fact]
    public void 每种数值类型的寄存器宽度都由合并边界锁定()
    {
        // RegisterWidth 决定"块结束位置"，而块结束位置是合并判定 gap < window 的唯一输入。
        // 因此宽度算错时块结构会变。变异实验实测的盲区：原用例只以 Word 覆盖了宽度 1、
        // 以 Real 覆盖了宽度 2，于是 RegisterWidth(Real) 2→1、RegisterWidth(SInt) 1→2
        // 两个变异都完全不可观测（82 条全绿）。本表把每个数值类型都钉进一个真值用例。
        //
        // 每个用例用**两个薄探针**把宽度 W 夹住。设同一对点 (base, base+W+1)：
        //   宽度正确（W） → 块终点 = base+W+1，间隔 1 < 2 → 单块，Length = W+2   …… 探针甲
        //   宽度偏大（W+1）→ 块终点 = base+W+2，间隔 0 < 2 → 单块，Length = W+3
        //   宽度偏小（W-1）→ 块终点 = base+W，  间隔 2 ≮ 2 → 拆两块
        // 再用第二对点 (base, base+W+2)：
        //   宽度正确（W） → 间隔 2 ≮ 2 → 拆两块                                  …… 探针乙
        //   宽度偏小（W-1）→ 间隔 1 < 2 → 单块，Length = W+2
        // 甲、乙两条断言合起来使 W-1 与 W+1 **两个方向都必然可观测**。
        //
        // ⚠️ 实测教训（本用例改了两版才对）：只用一个探针时，宽度的两个方向里总有一个
        // 恰好与期望值重合而不可观测——第一版几何 (base+W, 期望 W+1) 漏掉了 Real 的 2→1；
        // 第二版几何 (base+W+1, 期望 W+2) 又漏掉了 SInt 的 1→2。两次都是 82 条全绿。
        var widths = new (PointDataType Type, int Width)[]
        {
            (PointDataType.Bool, 1),
            (PointDataType.Byte, 1),
            (PointDataType.Word, 1),
            (PointDataType.SInt, 1),
            (PointDataType.USInt, 1),
            (PointDataType.Int, 1),
            (PointDataType.UInt, 1),
            (PointDataType.DWord, 2),
            (PointDataType.DInt, 2),
            (PointDataType.UDInt, 2),
            (PointDataType.Real, 2),
            (PointDataType.LReal, 4),
        };

        for (var i = 0; i < widths.Length; i++)
        {
            var (type, width) = widths[i];
            var start = 100 + (50 * i);

            // 探针甲：下一个点落在 base+W+1，期望合并成一块、长度 W+2
            var probeA = new[]
            {
                Point(1, start, type),
                Point(2, start + width + 1, PointDataType.Word),
            };

            var blocksA = ReadBlockPlanner.PlanForModbus(probeA, mergeWindowRegisters: 2, maxRegistersPerRequest: 120);

            blocksA.Should().ContainSingle(
                $"[甲] {type}（宽度 {width}）与其后第 {width + 1} 个寄存器的点间隔 1，必须合并成一个块");
            blocksA[0].StartAddress.Should().Be(start);
            blocksA[0].Length.Should().Be(
                width + 2,
                $"[甲] {type} 的寄存器宽度必须是 {width}；取 {width + 1} 会让块长为 {width + 3}");

            // 探针乙：下一个点落在 base+W+2，期望间隔 2 顶到窗口边界而拆成两块
            var probeB = new[]
            {
                Point(1, start, type),
                Point(2, start + width + 2, PointDataType.Word),
            };

            var blocksB = ReadBlockPlanner.PlanForModbus(probeB, mergeWindowRegisters: 2, maxRegistersPerRequest: 120);

            blocksB.Should().HaveCount(
                2,
                $"[乙] {type} 的寄存器宽度必须是 {width}；若被算小，间隔会降到 1 而被窗口吸收，两块会并成一块");
            blocksB[0].StartAddress.Should().Be(start);
            blocksB[1].StartAddress.Should().Be(start + width + 2);
        }
    }

    [Fact]
    public void 重复地址不会导致点丢失()
    {
        var points = new[] { Point(1, 100), Point(2, 100) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.SelectMany(b => b.Points).Should().HaveCount(2);
    }

    [Fact]
    public void 重复地址按_PointId_稳定排序()
    {
        // 同一地址有多个点时，计划里的顺序必须是确定的（按 PointId）。
        // 这条把当前的可观测行为固化下来；诚实标注它的**判别力边界**：
        // 实现用的是 LINQ OrderBy，其排序本身是稳定的，即使删掉 ThenBy(PointId)，
        // 本用例在当前输入下仍会通过（已实测：删掉 ThenBy(p=>p.PointId) 时本条与
        // "重复地址不会导致点丢失"都不红，红的是改 ThenByDescending 的那种变异）。
        // 即：本条锁的是"顺序确定且为按 PointId 升序"这一契约，不是"ThenBy 存在"。
        var first = Point(1, 100);
        var second = Point(2, 100);

        var blocks = ReadBlockPlanner.PlanForModbus(new[] { first, second }, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.SelectMany(b => b.Points).Should().ContainInOrder(first, second);
    }

    [Fact]
    public void 每个块内点按地址升序排列()
    {
        // ReadBlockPlanner 的 XML 注释承诺"内部会按地址排序"。这条锁住该承诺。
        // 乱序会静默破坏块规划与解码偏移的线性推导（解码器按块内顺序累加偏移）。
        var points = new[]
        {
            Point(1, 300), Point(2, 100), Point(3, 200), Point(4, 101),
        };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        foreach (var block in blocks)
        {
            block.Points.Select(p => p.Modbus!.RegisterAddress)
                .Should().BeInAscendingOrder("块内点必须按寄存器地址升序");
        }
    }

    // ===== 以下为控制者扫描要求补充的用例（brief 未覆盖） =====

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void 合并窗口小于_1_时抛_ArgumentOutOfRangeException(int mergeWindowRegisters)
    {
        var points = new[] { Point(1, 100) };

        var act = () => ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters, maxRegistersPerRequest: 120);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("mergeWindowRegisters")
            .WithMessage("*合并窗口必须至少为 1*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void 单请求寄存器上限小于_1_时抛_ArgumentOutOfRangeException(int maxRegistersPerRequest)
    {
        var points = new[] { Point(1, 100) };

        var act = () => ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxRegistersPerRequest")
            .WithMessage("*单次请求上限必须至少为 1*");
    }

    [Fact]
    public void 参数校验发生在遍历点集之前()
    {
        // 点集是非惰性的数组，但调用方传 IEnumerable 的惰性序列时，
        // 参数错误必须在枚举点集之前就抛出（否则会先把点集枚举一遍）。
        var enumerated = false;

        IEnumerable<PointConfig> Lazy()
        {
            enumerated = true;
            yield return Point(1, 100);
        }

        var act = () => ReadBlockPlanner.PlanForModbus(Lazy(), mergeWindowRegisters: 0, maxRegistersPerRequest: 120);

        act.Should().Throw<ArgumentOutOfRangeException>();
        enumerated.Should().BeFalse("参数非法时不应触碰点集");
    }

    [Fact]
    public void points_为_null_时抛_ArgumentNullException()
    {
        var act = () => ReadBlockPlanner.PlanForModbus(null!, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        act.Should().Throw<ArgumentNullException>().WithParameterName("points");
    }

    [Fact]
    public void 没有_Modbus_地址的点被排除在计划之外()
    {
        // 这是【有意的】行为：PlanForModbus 只处理 Modbus 点，Modbus == null 的点
        // （例如纯 S7 点）会被 Where 静默排除，不报错也不计数。
        // 锁定它的目的是：① 防止将来有人把该过滤条件误删——删掉后 PlanForModbus 会立刻
        // 对 null 地址抛 NullReferenceException（已由变异 M6 实测：失败 2 条，签名即该 NRE）；
        // ② 把"少了点却没有信号"这一后果显式记录在案，见实现文件里的 XML 注释。
        var s7Only = new PointConfig(
            PointId: 1, PointCode: "s7", PointName: "纯S7点", ColumnName: "s7",
            DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
            Enabled: true, S7: new S7Address(S7Area.DataBlock, 1, 0, 0), Modbus: null);
        var modbus = Point(2, 100);

        var blocks = ReadBlockPlanner.PlanForModbus(new[] { s7Only, modbus }, 16, 120);

        blocks.Should().ContainSingle();
        blocks[0].Points.Should().ContainSingle().Which.Should().BeSameAs(modbus);
        blocks.SelectMany(b => b.Points).Should().NotContain(s7Only);
    }

    [Fact]
    public void 全部点都没有_Modbus_地址时返回空计划()
    {
        var s7Only = new PointConfig(
            PointId: 1, PointCode: "s7", PointName: "纯S7点", ColumnName: "s7",
            DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
            Enabled: true, S7: new S7Address(S7Area.DataBlock, 1, 0, 0), Modbus: null);

        ReadBlockPlanner.PlanForModbus(new[] { s7Only }, 16, 120).Should().BeEmpty();
    }

    [Fact]
    public void 每个块内所有点都落在块覆盖的地址范围内()
    {
        // 规格 9 节最危险失效模式的结构性前提：解码时用 byteOffset = (点寄存器 - 块起始寄存器) * 2
        // 来定位。若某点落在 [StartAddress, StartAddress + Length) 之外，这个偏移就是负的或越界的，
        // 而 DecodeNumeric 只会返回 null（"坏点"）——数据丢失且看起来像偶发读失败。
        var points = new[]
        {
            Point(1, 100), Point(2, 101), Point(3, 110, PointDataType.Real),
            Point(4, 200), Point(5, 300, PointDataType.DWord), Point(6, 301),
        };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        foreach (var block in blocks)
        {
            block.Length.Should().BeGreaterThan(0);
            foreach (var point in block.Points)
            {
                var offset = point.Modbus!.RegisterAddress;
                offset.Should().BeGreaterThanOrEqualTo(
                    block.StartAddress, "点的寄存器地址不能落在块起点之前，否则解码偏移为负");
                offset.Should().BeLessThan(
                    block.StartAddress + block.Length, "点的寄存器地址必须落在块覆盖范围内，否则解码越界");
            }
        }

        blocks.SelectMany(b => b.Points).Should().BeEquivalentTo(points, "合并只是打包，不能丢点也不能重复");
    }

    private static PointConfig Point(int id, int register, PointDataType type = PointDataType.Word) => new(
        PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
        DataType: type, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, register));
}
