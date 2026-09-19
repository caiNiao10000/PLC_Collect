using System.Buffers.Binary;
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 字节数组 → 数值。规格 9 节标记的最危险失效模式就在这一层：
/// 合并读回多余字节后若解码错位，数据是错的但不会报错。
/// 因此所有解码都必须显式传 byteOffset，且严格做长度检查。
/// </summary>
/// <remarks>
/// **不支持的数据类型一律响亮抛 <see cref="NotSupportedException"/>，绝不静默返回 null。**
/// <list type="bullet">
///   <item><see cref="PointDataType.Dtl"/>（S7 <c>DATE_AND_TIME</c>，8 字节 BCD）、
///         <see cref="PointDataType.String"/>：本期不实现数值解码。</item>
///   <item><see cref="PointDataType.Bool"/>：不是数值类型，必须走 <see cref="DecodeBool"/>。</item>
/// </list>
/// 静默返回 null 是最坏的选择：调用方（采集器）无法区分"这个类型我不会解"与
/// "本次读失败，下轮再试"，于是会把它当坏点写入，表现为**配置看起来完全合法、
/// 数据就是没有**，没有任何信号。抛异常则立刻暴露。
/// 详见 task-6-report.md §5（方案 B 的选择理由）。
/// </remarks>
public static class ByteDecoder
{
    /// <summary>
    /// 解码一个数值。缓冲区不足时返回 null（视为坏点），绝不抛越界异常。
    /// </summary>
    /// <param name="buffer">本次读请求返回的原始字节（对应 ReadBlock 覆盖的整段范围）</param>
    /// <param name="byteOffset">该点在 buffer 中的起始字节偏移</param>
    /// <param name="type">数据类型</param>
    /// <param name="order">字节序</param>
    /// <exception cref="NotSupportedException">
    /// <paramref name="type"/> 为 <see cref="PointDataType.Dtl"/> / <see cref="PointDataType.String"/>（本期不支持），
    /// 或为 <see cref="PointDataType.Bool"/>（应改用 <see cref="DecodeBool"/>）。
    /// 该判定**先于长度检查**：即使缓冲区不足也照样抛异常，避免把配置错误伪装成坏点。
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> 不是已定义的枚举值。</exception>
    public static double? DecodeNumeric(
        ReadOnlySpan<byte> buffer,
        int byteOffset,
        PointDataType type,
        ByteOrder order)
    {
        // 先判类型支持性，再判长度：顺序是承重的，由
        // Dtl_与_String_即使缓冲区不足也抛异常而不是返回_null 测试锁定。
        EnsureSupportedForNumericDecode(type);

        var width = ByteWidth(type);

        if (byteOffset < 0 || byteOffset + width > buffer.Length)
        {
            return null;
        }

        var slice = buffer.Slice(byteOffset, width);

        return type switch
        {
            PointDataType.Real => DecodeSingle(slice, order),
            PointDataType.LReal => DecodeDouble(slice, order),
            PointDataType.Byte => slice[0],
            PointDataType.USInt => slice[0],
            PointDataType.SInt => (sbyte)slice[0],
            PointDataType.Word => DecodeUInt16(slice, order),
            PointDataType.UInt => DecodeUInt16(slice, order),
            PointDataType.Int => DecodeInt16(slice, order),
            PointDataType.DWord => DecodeUInt32(slice, order),
            PointDataType.UDInt => DecodeUInt32(slice, order),
            PointDataType.DInt => DecodeInt32(slice, order),
            // Bool / String / Dtl 已在 EnsureSupportedForNumericDecode 里拦下，
            // 走到这里只可能是未定义的枚举值。
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型"),
        };
    }

    /// <summary>
    /// 解码一个位。缓冲区不足时返回 null。
    /// </summary>
    /// <param name="buffer">原始字节</param>
    /// <param name="byteOffset">字节偏移</param>
    /// <param name="bitOffset">位偏移 0~7</param>
    public static bool? DecodeBool(ReadOnlySpan<byte> buffer, int byteOffset, int bitOffset)
    {
        if (byteOffset < 0 || byteOffset >= buffer.Length || bitOffset is < 0 or > 7)
        {
            return null;
        }

        return (buffer[byteOffset] & (1 << bitOffset)) != 0;
    }

    /// <summary>
    /// 某个数据类型在数值解码中占用的字节数。
    /// 仅覆盖 <see cref="DecodeNumeric"/> 支持的类型；<see cref="PointDataType.Bool"/>（应走
    /// <see cref="DecodeBool"/>）、<see cref="PointDataType.String"/>（长度随地址与配置而定）
    /// 与 <see cref="PointDataType.Dtl"/>（本期不支持）在 <see cref="DecodeNumeric"/> 入口即被拦下，
    /// 不会走到这里取宽度。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">不是已定义的枚举值。</exception>
    internal static int ByteWidth(PointDataType type) => type switch
    {
        // Bool / String / Dtl 已由 EnsureSupportedForNumericDecode 拦下，这里的宽度只为
        // 让本函数自身保持全枚举覆盖（若将来有人绕过守卫直接调用它）。Dtl 的 8 是它
        // 在 S7 里的真实长度（DATE_AND_TIME 8 字节），不是"可解码宽度"。
        PointDataType.Bool => 1,
        PointDataType.Byte => 1,
        PointDataType.SInt => 1,
        PointDataType.USInt => 1,
        PointDataType.Word => 2,
        PointDataType.Int => 2,
        PointDataType.UInt => 2,
        PointDataType.DWord => 4,
        PointDataType.DInt => 4,
        PointDataType.UDInt => 4,
        PointDataType.Real => 4,
        PointDataType.LReal => 8,
        PointDataType.Dtl => 8,
        PointDataType.String => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型"),
    };

    /// <summary>
    /// 拦截 <see cref="DecodeNumeric"/> 无法给出数值的类型。
    /// 每个消息都必须能回答两个问题：错在哪个类型、调用方该怎么做。
    /// </summary>
    private static void EnsureSupportedForNumericDecode(PointDataType type)
    {
        switch (type)
        {
            case PointDataType.Bool:
                throw new NotSupportedException(
                    "DecodeNumeric 不支持 Bool：BOOL 不是数值类型，请改用 ByteDecoder.DecodeBool(buffer, byteOffset, bitOffset)。" +
                    "本异常刻意不返回 null —— 若静默返回 null，BOOL 点会被当成坏点写入，配置看起来完全合法却永远没有数据。");

            case PointDataType.Dtl:
            case PointDataType.String:
                throw new NotSupportedException(
                    $"DecodeNumeric 本期不支持该数据类型：{type}。" +
                    "当前版本（Plan 1）只实现数值类型（BYTE/WORD/DWORD/SINT/USINT/INT/UINT/DINT/UDINT/REAL/LREAL）的字节解码；" +
                    "Dtl（S7 DATE_AND_TIME，8 字节 BCD）与 String（长度随地址与配置而定，无法由单一字节宽度表达）" +
                    "尚未实现，请改用其它数据类型，或等待后续版本。");

            default:
                return;
        }
    }

    private static float DecodeSingle(ReadOnlySpan<byte> slice, ByteOrder order)
    {
        var bits = order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt32BigEndian(slice)
            : BinaryPrimitives.ReadInt32LittleEndian(slice);

        return BitConverter.Int32BitsToSingle(bits);
    }

    private static double DecodeDouble(ReadOnlySpan<byte> slice, ByteOrder order)
    {
        var bits = order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt64BigEndian(slice)
            : BinaryPrimitives.ReadInt64LittleEndian(slice);

        return BitConverter.Int64BitsToDouble(bits);
    }

    private static ushort DecodeUInt16(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadUInt16BigEndian(slice)
            : BinaryPrimitives.ReadUInt16LittleEndian(slice);

    private static short DecodeInt16(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt16BigEndian(slice)
            : BinaryPrimitives.ReadInt16LittleEndian(slice);

    private static uint DecodeUInt32(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadUInt32BigEndian(slice)
            : BinaryPrimitives.ReadUInt32LittleEndian(slice);

    private static int DecodeInt32(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt32BigEndian(slice)
            : BinaryPrimitives.ReadInt32LittleEndian(slice);
}
