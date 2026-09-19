using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class ByteDecoderTests
{
    // 本文件里所有 fixture 的字节表示与期望值均**先实测后填写**（见 task-6-report.md 的探针输出），
    // 不采用任何凭推理推出的字节序列。原因是本项目已多次发生"凭推理写 fixture 细节写错"的教训。

    [Fact]
    public void 大端_REAL_解码正确()
    {
        // 25.5f 的大端表示：0x41CC0000
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Fact]
    public void 小端_REAL_解码正确()
    {
        byte[] buffer = [0x00, 0x00, 0xCC, 0x41];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Little)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Fact]
    public void 有偏移时解码正确()
    {
        byte[] buffer = [0xFF, 0xFF, 0x41, 0xCC, 0x00, 0x00, 0xFF];

        ByteDecoder.DecodeNumeric(buffer, 2, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x64 }, PointDataType.Word, 100.0)]      // 大端 100
    [InlineData(new byte[] { 0x64, 0x00 }, PointDataType.Word, 25600.0)]    // 大端 25600
    [InlineData(new byte[] { 0xFF, 0x9C }, PointDataType.Int, -100.0)]       // 大端有符号 -100
    public void 整数类型解码正确(byte[] buffer, PointDataType type, double expected)
    {
        ByteDecoder.DecodeNumeric(buffer, 0, type, ByteOrder.Big).Should().Be(expected);
    }

    // ===== 控制者扫描指出的类型覆盖缺口：以下类型此前零覆盖 =====
    // Byte / SInt / USInt / UInt / DWord / DInt / UDInt / LReal。
    // 其中 SInt（有符号 8 位）走的是与 Byte 完全不同的分支（(sbyte)slice[0]），
    // DInt 是有符号 32 位，LReal 走独立的 DecodeDouble 实现——三者都必须有真实边界值。

    [Theory]
    // --- 1 字节：Byte 与 USInt 同一分支；SInt 是独立的有符号分支 ---
    [InlineData(new byte[] { 0xFF }, PointDataType.Byte, 255.0)]        // Byte 上位
    [InlineData(new byte[] { 0x80 }, PointDataType.Byte, 128.0)]
    [InlineData(new byte[] { 0x00 }, PointDataType.Byte, 0.0)]
    [InlineData(new byte[] { 0xFF }, PointDataType.USInt, 255.0)]       // USInt 上位
    [InlineData(new byte[] { 0x7F }, PointDataType.USInt, 127.0)]
    [InlineData(new byte[] { 0xFF }, PointDataType.SInt, -1.0)]         // SInt 边界：-1（与 Byte 的 255 必须区分开）
    [InlineData(new byte[] { 0x80 }, PointDataType.SInt, -128.0)]       // SInt 下界
    [InlineData(new byte[] { 0x7F }, PointDataType.SInt, 127.0)]        // SInt 上界
    [InlineData(new byte[] { 0x9C }, PointDataType.SInt, -100.0)]
    // --- 2 字节：无符号 / 有符号 ---
    [InlineData(new byte[] { 0xFF, 0xFF }, PointDataType.Word, 65535.0)]     // Word 上位
    [InlineData(new byte[] { 0x80, 0x00 }, PointDataType.Word, 32768.0)]
    [InlineData(new byte[] { 0xFF, 0xFF }, PointDataType.UInt, 65535.0)]     // UInt 上位
    [InlineData(new byte[] { 0x80, 0x00 }, PointDataType.Int, -32768.0)]     // Int 下界
    [InlineData(new byte[] { 0x7F, 0xFF }, PointDataType.Int, 32767.0)]      // Int 上界
    [InlineData(new byte[] { 0xFF, 0xFF }, PointDataType.Int, -1.0)]
    // --- 4 字节：无符号 / 有符号 ---
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, PointDataType.DWord, 4294967295.0)]  // DWord 上位
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x00 }, PointDataType.DWord, 2147483648.0)]
    [InlineData(new byte[] { 0x00, 0x00, 0x64, 0x00 }, PointDataType.UDInt, 25600.0)]
    // UDInt 的**区分符号**探针：0x00006400 在有符号/无符号下同为 25600，不足以证明
    // UDInt 走的是无符号分支（把 UDInt 改成 DecodeInt32 时它不会红）。
    // 下面这条 0xFFFFFFFF 在无符号下是 4294967295、在有符号下是 -1，才真正把分支钉住。
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, PointDataType.UDInt, 4294967295.0)]
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x00 }, PointDataType.UDInt, 2147483648.0)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, PointDataType.DInt, -1.0)]           // DInt = -1，与 DWord/UDInt 的 4294967295 必须区分开
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x00 }, PointDataType.DInt, -2147483648.0)]  // DInt 下界 = int.MinValue
    [InlineData(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, PointDataType.DInt, 2147483647.0)]   // DInt 上界
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0x9C }, PointDataType.DInt, -100.0)]
    // --- 8 字节：LREAL 走独立的 DecodeDouble，此前从未被验证 ---
    [InlineData(new byte[] { 0x40, 0x39, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00 }, PointDataType.LReal, 25.5)]   // 0x4039800000000000
    [InlineData(new byte[] { 0xC0, 0x59, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00 }, PointDataType.LReal, -100.25)] // 0xC059100000000000
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, PointDataType.LReal, 0.0)]
    public void 大端各数值类型解码正确_含边界值(byte[] buffer, PointDataType type, double expected)
    {
        ByteDecoder.DecodeNumeric(buffer, 0, type, ByteOrder.Big).Should().Be(expected);
    }

    [Theory]
    // 小端覆盖所有多字节类型。注意：小端的定义是"整个值按字节逆序"
    // （等价于 BinaryPrimitives 的 Read*LittleEndian），不是"32 位值在 16 位寄存器间整体交换"。
    [InlineData(new byte[] { 0x9C, 0xFF }, PointDataType.Int, -100.0)]
    [InlineData(new byte[] { 0x64, 0x00 }, PointDataType.Word, 100.0)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x39, 0x40 }, PointDataType.LReal, 25.5)]
    [InlineData(new byte[] { 0x00, 0x64, 0x00, 0x00 }, PointDataType.UDInt, 25600.0)]
    [InlineData(new byte[] { 0x9C, 0xFF, 0xFF, 0xFF }, PointDataType.DInt, -100.0)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x80 }, PointDataType.DInt, -2147483648.0)]
    [InlineData(new byte[] { 0x00, 0x80, 0xC8, 0xC2 }, PointDataType.Real, -100.25)]
    public void 小端各多字节类型解码正确(byte[] buffer, PointDataType type, double expected)
    {
        ByteDecoder.DecodeNumeric(buffer, 0, type, ByteOrder.Little).Should().Be(expected);
    }

    [Fact]
    public void 缓冲区不足时返回_null_而不是读越界()
    {
        byte[] buffer = [0x41, 0xCC];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Big).Should().BeNull();
    }

    [Fact]
    public void 偏移超出缓冲区时返回_null()
    {
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        ByteDecoder.DecodeNumeric(buffer, 10, PointDataType.Real, ByteOrder.Big).Should().BeNull();
    }

    [Fact]
    public void 负偏移返回_null()
    {
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        ByteDecoder.DecodeNumeric(buffer, -1, PointDataType.Real, ByteOrder.Big).Should().BeNull();
    }

    // ===== 整数溢出带：长度检查必须用减法形式，加法形式会被回绕绕过 =====
    // 实测（修复前，加法形式 `byteOffset + width > buffer.Length`）：
    //   offset=10          → null    ✓
    //   offset=-1          → null    ✓
    //   offset=2147483643  → null    ✓（4 + 2147483643 == int.MinValue，回绕边界）
    //   offset=2147483644  → <ArgumentOutOfRangeException>  ⚠️ 违反契约
    //   offset=2147483647  → <ArgumentOutOfRangeException>  ⚠️ 违反契约
    // 后果是抛异常而不是错值（Span.Slice 自身用无溢出算术校验），但它**证伪了本方法
    // "缓冲区不足返回 null、绝不抛越界异常"的契约**。调用方（采集器的坏点处理）按契约
    // 预期"越界 = 本轮坏点、下轮再试"，拿到异常就是采集中断——而溢出恰恰来自调用方偏移算错，
    // 那正是最该被降级成"坏点"的场景。

    [Theory]
    [InlineData(int.MaxValue)]                          // 必然回绕
    [InlineData(int.MaxValue - 1)]                      // 必然回绕
    [InlineData(int.MaxValue - 3)]                      // 恰好是"4 + x == int.MinValue"的回绕边界
    [InlineData(int.MaxValue - 4)]                      // 边界外侧一格：减法形式下仍必须为 null，不是 0 长度成功
    [InlineData(int.MaxValue - 100)]
    public void 极大正偏移返回_null_而不抛异常(int byteOffset)
    {
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        var act = () => ByteDecoder.DecodeNumeric(buffer, byteOffset, PointDataType.Real, ByteOrder.Big);

        act.Should().NotThrow("本方法承诺'缓冲区不足返回 null、绝不抛越界异常'，溢出带也不例外");
        act().Should().BeNull("极大偏移必然越界，只能是坏点");
    }

    [Theory]
    [InlineData(int.MaxValue, PointDataType.Word)]      // width 2
    [InlineData(int.MaxValue - 1, PointDataType.Byte)]  // width 1
    [InlineData(int.MaxValue, PointDataType.LReal)]     // width 8
    public void 极大正偏移对任意宽度都返回_null(int byteOffset, PointDataType type)
    {
        // 溢出边界随 width 移动（回绕点是 width + offset == int.MinValue），
        // 故必须覆盖多种宽度，不能只用 width=4 的 Real。
        byte[] buffer = new byte[8];

        var act = () => ByteDecoder.DecodeNumeric(buffer, byteOffset, type, ByteOrder.Big);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Theory]
    [InlineData(0b0000_0001, 0, true)]
    [InlineData(0b0000_0001, 1, false)]
    [InlineData(0b1000_0000, 7, true)]
    [InlineData(0b0000_0000, 3, false)]
    public void 位解码正确(byte value, int bitOffset, bool expected)
    {
        ByteDecoder.DecodeBool(new[] { value }, 0, bitOffset).Should().Be(expected);
    }

    [Fact]
    public void 位解码缓冲区不足时返回_null()
    {
        ByteDecoder.DecodeBool(ReadOnlySpan<byte>.Empty, 0, 0).Should().BeNull();
    }

    [Fact]
    public void 位解码字节偏移越界时返回_null()
    {
        byte[] buffer = [0xFF];

        ByteDecoder.DecodeBool(buffer, 1, 0).Should().BeNull("偏移 1 已超出 1 字节缓冲区");
        ByteDecoder.DecodeBool(buffer, -1, 0).Should().BeNull("负偏移必须返回 null 而不是回绕读到别的字节");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(31)]
    [InlineData(int.MinValue)]
    public void 位偏移超出_0_到_7_时返回_null(int bitOffset)
    {
        // S7/Modbus 的位偏移只有 0~7。越界时若不做范围检查，C# 的移位语义
        // （1 << 8 == 256，byte & 256 恒为 0；1 << 31 为负）会给出静默的错值：
        // bitOffset=8 永远返回 false，看起来像"这个位一直是 0"。
        byte[] buffer = [0xFF];

        ByteDecoder.DecodeBool(buffer, 0, bitOffset).Should().BeNull();
    }

    [Fact]
    public void 合并块中的空洞位置不会导致相邻点解码错位()
    {
        // 这是规格 9 节里标记为"最危险"的失效模式：
        // 100 处是 WORD，103 处是 REAL，中间 101~102 是空洞（一起读回来了）
        byte[] buffer =
        [
            0x00, 0x01,             // 偏移 0-1: WORD = 1          (寄存器 100)
            0xDE, 0xAD,             // 偏移 2-3: 空洞               (寄存器 101)
            0x41, 0xCC, 0x00, 0x00, // 偏移 4-7: REAL = 25.5       (寄存器 102-103)
        ];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Word, ByteOrder.Big).Should().Be(1.0);
        ByteDecoder.DecodeNumeric(buffer, 4, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Fact]
    public void 空洞中的字节若被误当成相邻点的数据会给出完全不同的值()
    {
        // 上一条测试证明"按正确偏移解码得到正确值"，但不证明"偏移错了会被发现"。
        // 本条用同一段 buffer 证明：偏移错位会静默给出一个**看似合理的错值**（这正是
        // 规格 9 节说"数据是错的但不会报错"的含义），故 byteOffset 必须由块规划精确给出。
        byte[] buffer =
        [
            0x00, 0x01,             // 偏移 0-1: WORD = 1          (寄存器 100)
            0xDE, 0xAD,             // 偏移 2-3: 空洞               (寄存器 101)
            0x41, 0xCC, 0x00, 0x00, // 偏移 4-7: REAL = 25.5       (寄存器 102-103)
        ];

        // 正确偏移 4 → 25.5；若有人误用偏移 2（忘了空洞占一个寄存器），
        // 0xDEAD0000 会被解成一个巨大的负数——不抛异常、不返回 null，就是错值。
        ByteDecoder.DecodeNumeric(buffer, 4, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
        ByteDecoder.DecodeNumeric(buffer, 2, PointDataType.Real, ByteOrder.Big)
            .Should().NotBeApproximately(25.5, 0.0001, "错位偏移必须解出不同的值，证明偏移是承重的");
    }

    // ===== ByteDecoder 对 Dtl / String 的处置（控制者扫描第 1 条） =====
    // 选择的方案是 B：**响亮抛 NotSupportedException**，而不是落进 switch 的 `_ => null`。
    // 理由见 task-6-report.md §5：静默 null 会被采集器当成"坏点"写入，
    // 表现为"配置看起来完全合法、数据就是没有"，没有任何信号。

    [Theory]
    [InlineData(PointDataType.Dtl)]
    [InlineData(PointDataType.String)]
    public void 不支持的数值解码类型必须响亮抛异常而不是静默返回_null(PointDataType type)
    {
        // 缓冲区给足 8 字节，排除"因为缓冲区不足才失败"的解释
        byte[] buffer = new byte[8];

        var act = () => ByteDecoder.DecodeNumeric(buffer, 0, type, ByteOrder.Big);

        act.Should().Throw<NotSupportedException>()
            .WithMessage($"*{type}*",
                "异常消息必须点名具体数据类型，否则排障时不知道是哪个点配置错了")
            .WithMessage("*本期不支持*", "消息必须说明这是本期范围限制，而不是数据或连接问题");
    }

    [Fact]
    public void Dtl_与_String_即使缓冲区不足也抛异常而不是返回_null()
    {
        // 锁定守卫顺序：不支持类型的判定必须先于长度检查。
        // 否则"缓冲区恰好不足"会把一次配置错误伪装成"本次读失败（坏点）"，
        // 而这两种情况的处置完全不同（前者要报警、后者等下一轮）。
        byte[] tooShort = [0x00];

        var dtl = () => ByteDecoder.DecodeNumeric(tooShort, 0, PointDataType.Dtl, ByteOrder.Big);
        var str = () => ByteDecoder.DecodeNumeric(tooShort, 0, PointDataType.String, ByteOrder.Big);

        dtl.Should().Throw<NotSupportedException>();
        str.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void BOOL_走_DecodeNumeric_同样响亮抛异常并指向_DecodeBool()
    {
        // brief 只点了 Dtl/String 两个类型，但 Bool 也落在同一个 `_ => null` 分支上：
        // DecodeNumeric(Bool) 此前会静默返回 null。BOOL 点的正确入口是 DecodeBool，
        // 故这里同样响亮失败，并在消息里指明正确入口。
        byte[] buffer = [0x01];

        var act = () => ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Bool, ByteOrder.Big);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*DecodeBool*", "必须告诉调用方 BOOL 应该走哪个入口");
    }

    [Fact]
    public void 未定义的枚举值抛_ArgumentOutOfRangeException()
    {
        byte[] buffer = new byte[8];

        var act = () => ByteDecoder.DecodeNumeric(buffer, 0, (PointDataType)9999, ByteOrder.Big);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("type");
    }
}
