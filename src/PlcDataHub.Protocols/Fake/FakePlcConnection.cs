using PlcDataHub.Core.Model;
using PlcDataHub.Protocols.Modbus;

namespace PlcDataHub.Protocols.Fake;

/// <summary>
/// 脚本化响应：按调用次序依次返回，用于精确构造现场故障序列
/// （连上 → 采两轮 → 读超时 → 断开 → 重连成功 → 恢复采集）。
/// </summary>
/// <param name="FailConnect">true 表示本次 Connect 失败</param>
/// <param name="FailRead">true 表示本次 Read 整体失败（所有点为坏点）</param>
/// <param name="ErrorMessage">失败时的错误信息</param>
/// <param name="DataByPointId">
/// 成功时各点的原始字节，键为 <see cref="PointConfig.PointId"/>。
/// <para>
/// ⚠️ 语义是**"该点自己的字节缓冲区"**，不是真实连接里那种"整块合并读回的范围"：
/// 数值点从第 0 字节开始解码；BOOL 点按该点的位偏移取位（见 <see cref="FakePlcConnection"/>）。
/// 因此同一个脚本不能同时表达"真实合并块的布局"与"逐点字节"——本类只做后者。
/// </para>
/// </param>
public sealed record FakePlcResponse(
    bool FailConnect,
    bool FailRead,
    string? ErrorMessage,
    IReadOnlyDictionary<int, byte[]> DataByPointId);

/// <summary>假连接的响应脚本。耗尽后重复返回最后一个响应。</summary>
public sealed record FakePlcScript(IReadOnlyList<FakePlcResponse> Responses);

/// <summary>
/// 假 PLC 连接。用途有两个：
/// ① 单元/状态机测试——不需要真实设备就能构造任意故障序列；
/// ② 现场无 PLC 时验证"建表 + 采集 + 入库"链路。
/// </summary>
/// <remarks>
/// <para>
/// <b>它是有状态的</b>：脚本按<b>调用次序</b>推进，`ConnectAsync` 与 `ReadAsync` <b>共用同一个游标</b>。
/// 写脚本时必须把连接尝试也数进去，否则整条序列会错位一格——而错位的表现是"值不对/坏点不对"，
/// 看起来像解码 bug。脚本耗尽后固定返回最后一个响应（便于写"一直正常/一直失败"的长期用例）。
/// </para>
/// <para>
/// <b>它不做任何地址校验</b>：脚本按 <see cref="PointConfig.PointId"/> 提供字节，点的 Modbus 从站号、
/// 寄存器区、寄存器地址一律不参与——那正是真实连接（ModbusTcpConnection）的职责，由那边的测试覆盖。
/// 假连接<b>忽略</b>这一点是有意的：它要能表达"某台设备返回了与地址表不符的字节"。
/// </para>
/// <para>
/// <b>解码口径与真实连接一致</b>：数值走 <see cref="ByteDecoder.DecodeNumeric"/>、BOOL 走
/// <see cref="ByteDecoder.DecodeBool"/>，返回的是<b>原始值</b>（未乘 Scale / 未加 Offset，
/// 规格 3.4 节规定工程值转换在落库前由上层做）。因此 `Dtl` / `String` 点在这里也会抛
/// <see cref="NotSupportedException"/>，而不是静默写坏值——与 ByteDecoder 的契约相同。
/// </para>
/// </remarks>
public sealed class FakePlcConnection : IPlcConnection
{
    private readonly FakePlcScript _script;
    private int _callIndex;

    public FakePlcConnection(FakePlcScript script)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));

        if (script.Responses.Count == 0)
        {
            throw new ArgumentException("响应脚本不能为空", nameof(script));
        }
    }

    public bool IsConnected { get; private set; }

    /// <summary>Connect 被调用的次数。用于验证重连逻辑真的在重连。</summary>
    public int ConnectAttempts { get; private set; }

    /// <summary>
    /// 建立连接。失败抛 <see cref="InvalidOperationException"/>（由采集器的重连逻辑处理）。
    /// <para>本方法<b>同步</b>抛异常（不开线程、不 await）：脚本化失败没有"等待"的语义。</para>
    /// </summary>
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        var response = Next();

        if (response.FailConnect)
        {
            IsConnected = false;
            throw new InvalidOperationException(response.ErrorMessage ?? "假连接：连接失败");
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取一组点。返回的字典为每个传入的点都给出条目，失败的点值为 null。
    /// </summary>
    /// <exception cref="InvalidOperationException">尚未连接（与真实连接同一语义）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    public Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (!IsConnected)
        {
            throw new InvalidOperationException("尚未连接，不能读取");
        }

        var response = Next();
        var values = new Dictionary<PointConfig, object?>(points.Count);

        if (response.FailRead)
        {
            IsConnected = false;

            // 规格 5.3 节：坏点写 NULL，绝不抛异常冒泡
            foreach (var point in points)
            {
                values[point] = null;
            }

            return Task.FromResult(new ReadResult(values));
        }

        foreach (var point in points)
        {
            if (!response.DataByPointId.TryGetValue(point.PointId, out var bytes))
            {
                values[point] = null;
                continue;
            }

            values[point] = Decode(point, bytes);
        }

        return Task.FromResult(new ReadResult(values));
    }

    public void Dispose() => IsConnected = false;

    /// <summary>
    /// 按点自己的字节缓冲区解码。
    /// <para>
    /// BOOL 的位来源：<b>S7 点取 <see cref="S7Address.BitOffset"/>；Modbus 位区点（线圈/离散输入）
    /// 取第 0 字节的位 0（该点就是一位）；Modbus 寄存器区点取 <see cref="ModbusAddress.BitOffset"/>，
    /// 按与真实连接**同一个** <c>ModbusRegisterBits</c> 映射落到 2 字节大端寄存器上</b>
    /// （位 0~7 在低字节、位 8~15 在高字节）。两边共用同一映射是刻意的：否则同一个脚本
    /// 在假连接与真实连接上会解出不同的位，用它做的端到端验证就没有意义了。
    /// 缓冲区不够长时 <see cref="ByteDecoder.DecodeBool"/> 返回 null（该点本轮坏值）。
    /// </para>
    /// </summary>
    private static object? Decode(PointConfig point, byte[] bytes)
    {
        if (point.DataType != PointDataType.Bool)
        {
            return ByteDecoder.DecodeNumeric(bytes, 0, point.DataType, point.ByteOrder);
        }

        var (byteOffset, bitInByte) = ResolveBoolPosition(point);

        return ByteDecoder.DecodeBool(bytes, byteOffset, bitInByte);
    }

    /// <summary>BOOL 点在该点自己的字节缓冲区里的 (字节偏移, 字节内位偏移)。</summary>
    private static (int ByteOffset, int BitInByte) ResolveBoolPosition(PointConfig point)
    {
        if (point.Modbus is { } modbus)
        {
            return modbus.Area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput
                ? (0, 0)
                : (ModbusRegisterBits.ByteIndexForBit(modbus.BitOffset), modbus.BitOffset % 8);
        }

        return (0, point.S7?.BitOffset ?? 0);
    }

    /// <summary>取下一个响应；脚本耗尽后重复返回最后一个，便于写长期测试。</summary>
    private FakePlcResponse Next()
    {
        var index = Math.Min(_callIndex, _script.Responses.Count - 1);
        _callIndex++;
        return _script.Responses[index];
    }
}
