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
/// <para>
/// **没有某个点的条目 = 该点本轮坏值，而连接保持**——这正是真实连接里"某个读块被从站拒绝"
/// （<c>SlaveException</c>）的形态，故"部分点坏、其余点正常"用它表达。
/// </para>
/// </param>
/// <param name="KeepConnected">
/// <b>读失败时是否保持连接。</b>
/// <list type="bullet">
///   <item><c>true</c>：模拟**从站用异常码拒绝了请求**（真实连接里的 <c>SlaveException</c> 形态）——
///         点是坏值，但链路是好的，<see cref="IPlcConnection.IsConnected"/> 保持 true，
///         <see cref="IPlcConnection.LastError"/> 的类别是 <see cref="ConnectionFailureKind.SlaveRejected"/>，
///         采集器**不该**因此重连。</item>
///   <item><c>false</c>（默认）：模拟**传输层故障**——连接被标记断开，
///         <see cref="IPlcConnection.LastError"/> 的类别是 <see cref="ConnectionFailureKind.Transport"/>，
///         采集器**必须**重连。</item>
/// </list>
/// 两个值分别对应真实连接的两种失败分支，**不是风格差异**：Plan 2 用假连接验证重连策略时，
/// 若只能用 <c>false</c> 一种形态，结论会与真实设备相反。
/// </param>
public sealed record FakePlcResponse(
    bool FailConnect,
    bool FailRead,
    string? ErrorMessage,
    IReadOnlyDictionary<int, byte[]> DataByPointId,
    bool KeepConnected = false);

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
/// <b>它与真实连接保持同一套"协议语义与校验"</b>（否则 Plan 2 用假连接做的验证会假绿）：
/// <list type="bullet">
///   <item><b>协议归属</b>：不属于本连接协议的点（例如 Modbus 连接收到纯 S7 点）与真实连接一样**写坏值**，
///         不照脚本给值。构造时用 <paramref name="protocol"/> 指定本假连接扮演的协议，默认 Modbus TCP。</item>
///   <item><b>配置期校验</b>：Modbus 点走与真实连接**同一处**
///         <see cref="ModbusAddressValidator.EnsureAddressable"/>（从站号、地址、位偏移、类型与区的组合），
///         并且与真实连接一样**在任何 I/O（消耗脚本）之前**抛出。</item>
///   <item><b>失败类别</b>：脚本读失败可表达"坏点但连接保持"（<see cref="FakePlcResponse.KeepConnected"/>），
///         与真实连接的 <c>SlaveException</c> 分支同形；<see cref="IPlcConnection.LastError"/> 同步填充。</item>
/// </list>
/// </para>
/// <para>
/// <b>解码口径与真实连接一致</b>：数值走 <see cref="ByteDecoder.DecodeNumeric"/>、BOOL 走
/// <see cref="ByteDecoder.DecodeBool"/>，BOOL 在寄存器区的位映射与真实连接**共用**
/// <c>ModbusRegisterBits</c>（否则同一个脚本在两边会解出不同的位）。返回的是<b>原始值</b>
/// （未乘 Scale / 未加 Offset，规格 3.4 节规定工程值转换在落库前由上层做）。
/// </para>
/// <para>
/// ⚠️ <b>它仍与真实连接有一处结构性差异</b>：脚本按"点自己的字节缓冲区"给数据，
/// 而真实连接解码的是"整块合并读回的字节流"。**用假连接验证不了"读块合并的字节偏移"**——
/// 那部分由 <c>ModbusTcpConnectionTests</c> 与 <c>ModbusTcpWireTests</c> 覆盖。
/// </para>
/// </remarks>
public sealed class FakePlcConnection : IPlcConnection
{
    private readonly FakePlcScript _script;
    private readonly ProtocolKind _protocol;
    private int _callIndex;

    /// <param name="script">响应脚本，不能为空。</param>
    /// <param name="protocol">
    /// 本假连接扮演的协议，默认 <see cref="ProtocolKind.ModbusTcp"/>（Plan 1 唯一实现的协议）。
    /// <b>必须与实际要验证的连接协议一致</b>，否则"按协议分发点集"这类验证会假绿：
    /// 不属于该协议的点一律写坏值，与真实连接相同。
    /// </param>
    /// <exception cref="ArgumentException">脚本为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocol"/> 不是已定义的协议值。</exception>
    public FakePlcConnection(FakePlcScript script, ProtocolKind protocol = ProtocolKind.ModbusTcp)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));

        if (script.Responses.Count == 0)
        {
            throw new ArgumentException("响应脚本不能为空", nameof(script));
        }

        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocol), protocol,
                "未知协议值：无法判断本假连接该扮演哪种协议的设备，也就无法与真实连接保持同一套协议语义。");
        }

        _protocol = protocol;
    }

    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public ConnectionFailure? LastError { get; private set; }

    /// <summary>Connect 被调用的次数。用于验证重连逻辑真的在重连（含失败的尝试）。</summary>
    public int ConnectAttempts { get; private set; }

    /// <summary>
    /// 建立连接。失败抛 <see cref="InvalidOperationException"/>（由采集器的重连逻辑处理），
    /// 并写入 <see cref="LastError"/>（类别 <see cref="ConnectionFailureKind.Transport"/>）。
    /// <para>本方法<b>同步</b>抛异常（不开线程、不 await）：脚本化失败没有"等待"的语义。</para>
    /// </summary>
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        var response = Next();

        if (response.FailConnect)
        {
            IsConnected = false;

            var message = response.ErrorMessage ?? "假连接：连接失败";
            LastError = new ConnectionFailure(ConnectionFailureKind.Transport, message);
            throw new InvalidOperationException(message);
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 读取一组点。返回的字典为每个传入的点都给出条目，失败的点值为 null。
    /// </summary>
    /// <remarks>
    /// 执行顺序与真实连接一致：**先按协议筛点 → 再校验配置 → 最后才消耗脚本（发请求）**。
    /// 故配置错误抛出时脚本游标不动，后续读取不会因为"被拒绝的一次调用"而错位。
    /// </remarks>
    /// <exception cref="InvalidOperationException">尚未连接（与真实连接同一语义）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">Modbus 点的从站号/地址/位偏移非法（与真实连接同一处校验）。</exception>
    /// <exception cref="NotSupportedException">Modbus 点的类型与寄存器区组合无法读取，或类型本期不支持。</exception>
    public Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (!IsConnected)
        {
            throw new InvalidOperationException("尚未连接，不能读取");
        }

        var ownedPoints = new List<PointConfig>(points.Count);

        foreach (var point in points)
        {
            if (!BelongsToProtocol(point))
            {
                // 不属于本协议的点：与真实连接一样写坏值（真实连接里这些点是 Modbus 为 null 的点）。
                continue;
            }

            ValidatePoint(point);
            ownedPoints.Add(point);
        }

        var response = Next();
        var values = new Dictionary<PointConfig, object?>(points.Count);

        if (response.FailRead)
        {
            if (!response.KeepConnected)
            {
                IsConnected = false;
            }

            // 规格 5.3 节：坏点写 NULL，绝不抛异常冒泡。
            // 是否断开连接由脚本决定——两种形态对应真实连接的两种失败分支（见 FakePlcResponse）。
            LastError = new ConnectionFailure(
                response.KeepConnected ? ConnectionFailureKind.SlaveRejected : ConnectionFailureKind.Transport,
                response.ErrorMessage ?? "假连接：读失败");

            foreach (var point in points)
            {
                values[point] = null;
            }

            return Task.FromResult(new ReadResult(values));
        }

        foreach (var point in ownedPoints)
        {
            if (!response.DataByPointId.TryGetValue(point.PointId, out var bytes))
            {
                values[point] = null;
                continue;
            }

            values[point] = Decode(point, bytes);
        }

        // 不属于本协议的点、以及脚本没有给字节的点，都要有条目（契约：每个传入的点都有条目）。
        foreach (var point in points)
        {
            if (!values.ContainsKey(point))
            {
                values[point] = null;
            }
        }

        return Task.FromResult(new ReadResult(values));
    }

    public void Dispose() => IsConnected = false;

    /// <summary>
    /// 该点是否属于本假连接扮演的协议。不属于的点与真实连接一样写坏值——
    /// 真实连接里"纯 S7 点交给 Modbus 连接"就是这个形态。
    /// </summary>
    private bool BelongsToProtocol(PointConfig point) => _protocol switch
    {
        ProtocolKind.ModbusTcp or ProtocolKind.ModbusRtu => point.Modbus is not null,
        ProtocolKind.S7 => point.S7 is not null,
        _ => throw new ArgumentOutOfRangeException(
            nameof(_protocol), _protocol, "未知协议值（构造期已校验，正常不可达）。"),
    };

    /// <summary>
    /// Modbus 点走与真实连接**同一处**校验。S7 侧暂无校验器（Task 7 未实现 S7 连接），故只筛不校。
    /// </summary>
    private void ValidatePoint(PointConfig point)
    {
        if (_protocol is ProtocolKind.ModbusTcp or ProtocolKind.ModbusRtu)
        {
            ModbusAddressValidator.EnsureAddressable(point);
        }
    }

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
