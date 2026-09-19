using System.Net.Sockets;
using NModbus;
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols.Modbus;

/// <summary>Modbus TCP 采集选项。默认值与规格 5.2 节一致。</summary>
/// <param name="MergeWindowRegisters">合并窗口（寄存器）。语义与边界见 <see cref="ReadBlockPlanner.PlanForModbus"/>。</param>
/// <param name="MaxRegistersPerRequest">
/// 单请求寄存器上限。**上限 125 是 Modbus 协议本身的限制**（功能码 03/04 的响应里字节数是 1 字节，
/// 最多 250 字节数据 = 125 个寄存器）；超限时从站会回异常码或 NModbus 直接拒绝，
/// 而那会在采集期表现为"某个读块永远失败"——归因不到"上限配大了"。故在构造期响亮拦住。
/// <para>
/// ⚠️ 同一个值也约束**位区**（线圈/离散输入）的单次请求**位数**：位区每个地址只占一位，
/// 于是默认 120 的含义是"一次最多读 120 个线圈"。功能码 01/02 协议上允许 2000 个线圈，
/// 所以这个默认值对位区是**保守**的（多分几次请求，既不越协议限也不会读错）。
/// 若将来要给位区单独放宽，需要扩本 record（Plan 1 不做）。
/// 该行为由 <c>位区的单次请求位数也受同一上限约束</c> 用例钉住。
/// </para>
/// </param>
/// <param name="TimeoutMs">
/// 单次请求超时（毫秒）。**必须 ≥ 1**：0 在 .NET 的 socket 语义里是"无限等待"，
/// 会让采集线程永久挂住（没有超时就没有重连），故在构造期响亮拦住。
/// </param>
public sealed record ModbusTcpOptions(
    int MergeWindowRegisters = 16,
    int MaxRegistersPerRequest = 120,
    int TimeoutMs = 1000);

/// <summary>
/// Modbus TCP 连接。规格 5.1 节：本类实例由单个采集线程独占，
/// 内部的 socket 绝不并发访问——多于一个线程同时用它必然串帧。
/// </summary>
/// <remarks>
/// <para>
/// <b>读取是同步阻塞的</b>（NModbus 的同步 API 会一直等到响应或 <see cref="ModbusTcpOptions.TimeoutMs"/> 到期），
/// 本类只把它包成已完成的 <see cref="Task"/>。因此 <c>cancellationToken</c> <b>无法中断正在进行的单次网络读</b>，
/// 只能在开始一轮、以及两个读块之间生效；单次读的上界由 <see cref="ModbusTcpOptions.TimeoutMs"/> 保证。
/// </para>
/// <para>
/// <b>分组是承重的</b>：读块必须按 <c>(SlaveId, ModbusRegisterArea)</c> 分组后再交给
/// <see cref="ReadBlockPlanner.PlanForModbus"/>。规划器本身<b>不按从站分组、也不校验寄存器区</b>，
/// 若把不同从站/不同区的点一起交给它，它会合并出一个跨从站、跨功能码的请求：
/// <list type="bullet">
///   <item>跨从站：整组用第一个点的从站号去读 → <b>从站 2 的点拿到从站 1 的数据，无任何错误信号</b>；</item>
///   <item>跨区：寄存器区之间的地址编号不通用（保持寄存器 100 ≠ 输入寄存器 100 ≠ 线圈 100）。</item>
/// </list>
/// </para>
/// <para>
/// <b>BOOL 点有两条物理路径</b>，选哪条由点的寄存器区决定：
/// <list type="table">
///   <listheader><term>点的 Area</term><description>读取方式 / 位提取</description></listheader>
///   <item><term><see cref="ModbusRegisterArea.Coil"/> / <see cref="ModbusRegisterArea.DiscreteInput"/></term>
///         <description>功能码 01/02 读**位**（<c>ReadCoils</c>/<c>ReadInputs</c> 返回 <c>bool[]</c>，
///         索引 i 即地址 start+i）。此时 <see cref="ModbusAddress.BitOffset"/> 必须为 0——一个线圈就是一位。</description></item>
///   <item><term><see cref="ModbusRegisterArea.HoldingRegister"/> / <see cref="ModbusRegisterArea.InputRegister"/></term>
///         <description>功能码 03/04 读**16 位字**，再把寄存器值按大端拆成字节流、取
///         <see cref="ModbusAddress.BitOffset"/>（0~15）位。映射见 <see cref="ModbusRegisterBits"/>。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>异常策略（分成三类，各有明确后果）</b>：
/// <list type="table">
///   <listheader><term>异常</term><description>处置与理由</description></listheader>
///   <item><term><see cref="ArgumentOutOfRangeException"/> / <see cref="NotSupportedException"/>（配置错误）</term>
///         <description><b>在发出任何请求之前</b>对全部点校验并直接抛出
///         （见 <see cref="ModbusAddressValidator.EnsureAddressable"/>），
///         使"这个配置永远不可能工作"不至于被伪装成"本周期读失败"。
///         **这类检查包含 Dtl / String**：它们能通过规划（占 4 / 1 个寄存器），若留到解码期才抛，
///         异常会发生在一次白发的请求之后、且不含点标识。</description></item>
///   <item><term><see cref="SlaveException"/></term>
///         <description>从站**答复了**异常码（如非法数据地址）：链路是好的，坏的是这个块的请求。
///         该块的点写 NULL，<b>连接保持 IsConnected = true</b>——否则采集器会每周期无谓重连一次。
///         同时写入 <see cref="LastError"/>（类别 <see cref="ConnectionFailureKind.SlaveRejected"/>），
///         否则"某点每周期静默 NULL"与"偶发读失败"完全不可区分。</description></item>
///   <item><term>传输类异常（超时/连接被重置/读写失败…）</term>
///         <description>该块的点写 NULL、<b>IsConnected 置 false</b>、写入 <see cref="LastError"/>；
///         **本轮剩余块一律不再发请求**（见 <see cref="ReadAsync"/> 的 linkDown 说明），
///         之后必须重新 <see cref="ConnectAsync"/> 才能继续读。</description></item>
///   <item><term>**未预期的异常**（不在传输白名单、也不是配置错误）</term>
///         <description>**原样冒泡**（绝不降级成坏点：那会把库/代码缺陷伪装成"偶发读失败"），
///         但**同样把 IsConnected 置 false** 并写入 <see cref="LastError"/>
///         （类别 <see cref="ConnectionFailureKind.Unexpected"/>）。
///         这条是修复轮 2 补的：实测设备消失/对端 RST 之后 NModbus 从第二次调用起恒抛
///         <see cref="InvalidOperationException"/>（不在白名单里）——
///         不标失效就会"每轮抛同一个异常、永不重连、采集彻底停摆"。</description></item>
/// </list>
/// 取消（<see cref="OperationCanceledException"/>）与上述各类都不同：**一律冒泡且不标失效**，
/// 它不是"这个点本轮坏了"，也不是链路的问题。
/// </para>
/// <para>
/// <b>IsConnected 的恢复语义（只有 <see cref="ConnectAsync"/> 能把它置回 true）</b>：
/// 一次传输失败就意味着"这条连接对象不可再信"——**读超时后从站可能仍会把那次响应发回来**，
/// 复用同一个 socket 会让下一个请求捡到上一条迟到帧（错值且不报错）。
/// 故：传输失败或未预期异常 → <c>IsConnected = false</c> → 下一轮 <see cref="ReadAsync"/> **抛
/// <see cref="InvalidOperationException"/>**（"尚未连接，不能读取"）→ 采集器的重连逻辑
/// 必须重新 <see cref="ConnectAsync"/>（它内部会释放旧 socket 并新建）。
/// **不存在"块失败后继续在同一 socket 上读下一轮"的路径**，因此也不会有"标死却还在用"的自相矛盾。
/// 配置错误是唯一的例外：它抛异常但**不**标失效（连接状态没有因此不可信），
/// 采集器修好配置即可继续用同一条连接。
/// </para>
/// <para>
/// <b>返回的是原始值</b>：未乘 Scale、未加 Offset（规格 3.4 节要求工程值转换在落库前完成）。
/// </para>
/// <para>
/// <b>已知限制（模型缺口，不在连接层做特例）</b>：
/// <list type="bullet">
///   <item><b>1 字节类型固定取寄存器的高字节</b>：<c>Byte</c>/<c>SInt</c>/<c>USInt</c> 在寄存器区的
///         字节偏移是 0，即寄存器大端拆字节后的**首字节（高字节）**；而 <c>ByteOrder</c> 对 1 字节类型
///         无意义（<see cref="ByteDecoder"/> 里 1 字节分支不取字节序）。若现场设备把单字节值放在寄存器的
///         **低**字节，本模型**表达不出来**——需要把它声明成 <c>WORD</c> 后在别处取舍，或扩模型。
///         这与 word-swapped 属同一类缺口，属规格 8.1 节登记的限制。</item>
///   <item><b>寄存器区非 BOOL 点的 BitOffset 被忽略</b>：它只在 BOOL 点上有意义（见
///         <see cref="ModbusAddress"/>）。刻意不报错——那份配置只是冗余，不产生错值，
///         见 <see cref="ModbusAddressValidator"/> 的说明。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ModbusTcpConnection : IPlcConnection
{
    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法下界。0 是广播地址，不能用于读取。</summary>
    public const int MinSlaveId = ModbusAddressValidator.MinSlaveId;

    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法上界。248~255 保留。</summary>
    public const int MaxSlaveId = ModbusAddressValidator.MaxSlaveId;

    /// <summary>功能码 03/04 单次最多读 125 个寄存器（响应字节数域只有 1 字节，最多 250 字节数据）。</summary>
    public const int MaxRegistersPerModbusRequest = 125;

    private readonly DeviceConnection _connection;
    private readonly ModbusTcpOptions _options;
    private readonly IModbusRequestChannel? _injectedChannel;
    private IModbusRequestChannel? _channel;

    public ModbusTcpConnection(DeviceConnection connection, ModbusTcpOptions? options = null)
        : this(connection, options, injectedChannel: null)
    {
    }

    /// <summary>
    /// 测试接缝：注入一个 <see cref="IModbusRequestChannel"/> 后，<see cref="ConnectAsync"/> 不建 socket，
    /// 直接进入"已连接"状态。生产代码只走上面那个公开构造函数。
    /// </summary>
    internal ModbusTcpConnection(
        DeviceConnection connection,
        ModbusTcpOptions? options,
        IModbusRequestChannel? injectedChannel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new ModbusTcpOptions();
        _injectedChannel = injectedChannel;

        if (connection.Protocol != ProtocolKind.ModbusTcp)
        {
            throw new ArgumentException($"协议必须是 ModbusTcp，实际为 {connection.Protocol}", nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            throw new ArgumentException("Modbus TCP 必须配置 Host", nameof(connection));
        }

        // 以下三个配置值在构造期就校验：它们错了以后，失败会发生在采集期且归因不到配置
        // （窗口/上限越小越隐蔽，超时 0 直接变成无限等待）。规划器内部还有一道同类校验，
        // 是防御而不是唯一防线。
        if (_options.MergeWindowRegisters < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MergeWindowRegisters, "合并窗口（MergeWindowRegisters）必须至少为 1。");
        }

        if (_options.MaxRegistersPerRequest is < 1 or > MaxRegistersPerModbusRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.MaxRegistersPerRequest,
                $"单请求寄存器上限（MaxRegistersPerRequest）必须在 1~{MaxRegistersPerModbusRequest} 之间："
                + $"上界是 Modbus 协议本身对功能码 03/04 的限制（响应字节数域 1 字节 = 最多 250 字节 = {MaxRegistersPerModbusRequest} 个寄存器），"
                + "超限的请求会被从站拒绝或截断，而采集期只会表现为'这个读块一直失败'。");
        }

        if (_options.TimeoutMs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.TimeoutMs,
                "单次请求超时（TimeoutMs）必须至少为 1 毫秒：0 在 .NET 的 socket 语义里表示无限等待，"
                + "会让采集线程永久挂住、永不触发重连。");
        }
    }

    public bool IsConnected { get; private set; }

    /// <inheritdoc />
    public ConnectionFailure? LastError { get; private set; }

    /// <summary>
    /// 建立连接。失败抛异常（含超时抛 <see cref="TimeoutException"/>），由采集器的重连逻辑处理；
    /// 失败原因同时写入 <see cref="LastError"/>（类别恒为 <see cref="ConnectionFailureKind.Transport"/>）。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_injectedChannel is not null)
        {
            // 测试注入路径：不建 socket，也不在这里释放注入的通道——重复 ConnectAsync 会把测试桩释放掉。
            // 注入通道随本对象的 Dispose 释放（测试正是靠 stub.DisposeCount 断言释放行为）。
            _channel = _injectedChannel;
            IsConnected = true;
            return;
        }

        DisposeChannel();

        var client = new TcpClient();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.TimeoutMs);

        try
        {
            await client.ConnectAsync(_connection.Host!, _connection.Port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();

            var timeout = new TimeoutException(
                $"连接 {_connection.Host}:{_connection.Port} 超时（{_options.TimeoutMs} ms）");
            LastError = new ConnectionFailure(ConnectionFailureKind.Transport, timeout.Message);
            throw timeout;
        }
        catch (OperationCanceledException)
        {
            // 调用方取消：不是连接的失败，不写 LastError。
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // 连接失败（拒绝/不可达）也要把 socket 放掉，否则句柄泄漏；原因留痕供运行状态展示。
            client.Dispose();
            LastError = new ConnectionFailure(
                ConnectionFailureKind.Transport,
                $"连接 {_connection.Host}:{_connection.Port} 失败：{ex.GetType().Name}: {ex.Message}");
            throw;
        }

        client.NoDelay = true;

        var master = new ModbusFactory().CreateMaster(client);
        master.Transport.ReadTimeout = _options.TimeoutMs;
        master.Transport.WriteTimeout = _options.TimeoutMs;

        _channel = new NModbusRequestChannel(master, client);
        IsConnected = true;
    }

    /// <summary>
    /// 读取一组点。返回的字典为每个传入的点都给出条目；失败的点、以及不属于本连接协议的点
    /// （<see cref="PointConfig.Modbus"/> 为 null）值为 null。
    /// </summary>
    /// <remarks>
    /// <b>传输故障后本轮不再发请求</b>：首个传输类失败会把本轮标记为"链路已失效"，
    /// 剩余块**直接标坏点、不再发请求**。否则每个块都要各自等满
    /// <see cref="ModbusTcpOptions.TimeoutMs"/> × NModbus 的重试（默认 3 次重试 + 250ms 间隔），
    /// 一轮十块能拖到几十秒，而这期间没有任何信号。
    /// 之后 <see cref="IsConnected"/> 为 false，下次调用本方法会抛 <see cref="InvalidOperationException"/>，
    /// 采集器必须重新 <see cref="ConnectAsync"/>（理由见类注释的"IsConnected 的恢复语义"）。
    /// <para>
    /// ⚠️ <b>中止的范围是"整轮"，含其它从站与其它寄存器区</b>（不是"只跳过同组的剩余块"）。
    /// 这么定的理由：危险的是**复用同一个 socket**，而 socket 是本连接独占的、与从站号无关——
    /// 一条 Modbus TCP 连接上所有从站共用它。故链路一旦失效，本轮继续读任何从站都是在同一条可疑 socket 上读。
    /// </para>
    /// <para>
    /// <b>代价有界（这是"整轮中止"可以接受的理由）</b>：中止时 <see cref="IsConnected"/> 已为 false，
    /// 下一轮调用本方法会**直接抛**（一个请求都不发），采集器必须重连——
    /// 于是"本可成功的那些块"最多**延后一轮**就会被发现并恢复，而不是被永久丢弃。
    /// 若将来要把它收窄成"只跳过同组"，需要先论证"同一个 socket 的部分失败不会污染其它从站"，
    /// 而这正是修复轮 1 所否定的假设。
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">尚未连接（含上一轮发生过传输故障的情形）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 某个点的从站号不在 1~247、寄存器地址越界、或位偏移越界
    /// （见 <see cref="ModbusAddressValidator.EnsureAddressable"/>）。
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// 某个点的类型与寄存器区组合无法读取（位区里的非 BOOL 点），或该点类型本期不支持
    /// （<see cref="PointDataType.Dtl"/> / <see cref="PointDataType.String"/>）。
    /// 两者都在**配置期**（发出任何请求之前）抛出，且消息点名到点。
    /// 这类异常**不会被逐块故障隔离吞掉**——它们是配置错误。
    /// </exception>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        var channel = _channel;

        if (channel is null || !IsConnected)
        {
            throw new InvalidOperationException("尚未连接，不能读取");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var values = new Dictionary<PointConfig, object?>(points.Count);

        // 单趟物化 + 全量校验：既避免"多次枚举同一个 IEnumerable"这一类缺陷
        // （Task 6 在规划器上踩过：单遍序列被枚举两趟会静默产出空计划），
        // 也让配置错误在任何 I/O 之前就抛出来。
        var modbusPoints = new List<PointConfig>(points.Count);

        foreach (var point in points)
        {
            if (point.Modbus is null)
            {
                // 不属于本连接的点：给条目、值为 null。与规划器的口径一致（它也会静默排除）。
                // 采集编排层按协议分发点集，纯 S7 点不该走到这里。
                continue;
            }

            ModbusAddressValidator.EnsureAddressable(point);
            modbusPoints.Add(point);
        }

        // 本轮链路是否已被判定失效。一旦为 true，剩余块只标坏点、不再发请求（见 ReadAsync 的 remarks）。
        var linkDown = false;

        foreach (var group in modbusPoints.GroupBy(p => (p.Modbus!.SlaveId, p.Modbus!.Area)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var groupPoints = group.ToList();
            var slaveId = (byte)group.Key.SlaveId;
            var area = group.Key.Area;

            // 规划器签名保持不变（跨任务约束 1）：分组是连接层的编排职责，不是规划器的。
            foreach (var block in ReadBlockPlanner.PlanForModbus(
                         groupPoints,
                         _options.MergeWindowRegisters,
                         _options.MaxRegistersPerRequest))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (linkDown)
                {
                    // 链路已判定失效：**不发请求**。否则每个剩余块都要各自等满
                    // TimeoutMs × NModbus 重试（默认 3 次 + 250ms 间隔 ≈ 单块 4.75s），
                    // 一轮十块能拖到几十秒，期间没有任何信号。
                    MarkBlockPointsBad(block, values);
                    continue;
                }

                if (!ReadBlockInto(channel, slaveId, area, block, values))
                {
                    linkDown = true;
                }
            }
        }

        // 没有被任何读块覆盖的点（非 Modbus 点，以及位/寄存器路径都没产出值的点）也要有条目。
        foreach (var point in points)
        {
            if (!values.ContainsKey(point))
            {
                values[point] = null;
            }
        }

        return Task.FromResult(new ReadResult(values));
    }

    public void Dispose()
    {
        DisposeChannel();
    }

    /// <summary>
    /// 读一个块并按区走对应的物理路径；逐块做故障隔离。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 链路仍可用（**含"从站用异常码拒绝了本块"**——从站答复了就说明链路是好的）；
    /// <c>false</c> = 传输层故障，调用方必须停止本轮剩余块的读取（见 <see cref="ReadAsync"/> 的 linkDown）。
    /// </returns>
    private bool ReadBlockInto(
        IModbusRequestChannel channel,
        byte slaveId,
        ModbusRegisterArea area,
        ReadBlock block,
        Dictionary<PointConfig, object?> values)
    {
        try
        {
            switch (area)
            {
                case ModbusRegisterArea.Coil:
                case ModbusRegisterArea.DiscreteInput:
                    ReadBitBlockInto(channel, slaveId, area, block, values);
                    break;

                case ModbusRegisterArea.HoldingRegister:
                case ModbusRegisterArea.InputRegister:
                    ReadRegisterBlockInto(channel, slaveId, area, block, values);
                    break;

                default:
                    // 已由 ModbusAddressValidator 拦下。这里再抛一次，是为了将来给 ModbusRegisterArea
                    // 加新枚举值时不会"默默用错功能码去读"——而它会被下面的 NotSupportedException
                    // 分支原样冒泡，绝不会变成 NULL。
                    throw new NotSupportedException(
                        $"未知的寄存器区 {area}（{(int)area}），无法确定读取用的功能码。");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // 取消不是"这个点本轮坏了"，而是整轮应当终止 → 必须冒泡。
            throw;
        }
        catch (NotSupportedException)
        {
            // 配置错误必须冒泡，绝不能在这里变成 NULL：把"这个配置永远不可能工作"伪装成
            // "偶发读取失败"，正是本项目最怕的失效（该点每周期静默写坏值、没有任何信号）。
            // 正常路径不可达（配置期已把这些配置拦下），保留为**第二道防线**：
            // 它拦的是"绕过配置校验直接调用通道"的将来代码，以及库自己抛出的同类型异常。
            //
            // 实测（修复轮 1 变异 R11）：**在当前传输白名单下，删掉本分支测试仍全绿**——
            // NotSupportedException 不在白名单里，没有别的 catch 会接住它，它本来就会冒泡。
            // 保留它的理由只有一条，但是真实的：本分支位于传输白名单分支**之前**，
            // 若将来有人把 NotSupportedException 误加进白名单、或在后面补一个 catch-all，
            // 这里仍会先把它拦住。该性质由 `读通道抛出_NotSupportedException_时不被吞成坏点` 覆盖。
            throw;
        }
        catch (SlaveException ex)
        {
            // 从站答复了异常码（例如非法数据地址 0x02）：设备与链路都是好的，坏的是这个块的请求。
            // 标记整条连接断开会让采集器每周期做一次无谓的重连。
            // 但"不重连"不等于"不留痕"：异常码 01/02 恰是现场最常见的**配置**错误形态，
            // 若只写 NULL，它就和偶发读失败完全不可区分 → 必须写 LastError。
            LastError = new ConnectionFailure(
                ConnectionFailureKind.SlaveRejected,
                $"从站 {slaveId} 拒绝了 {area} 区的读块（起始 {block.StartAddress}，长度 {block.Length}）："
                + $"从站异常码 {ex.SlaveExceptionCode}（功能码 {ex.FunctionCode}）。"
                + "链路是好的，请检查该块的地址/寄存器区配置。");

            MarkBlockPointsBad(block, values);
            return true;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // 传输层故障：链路已不可用 → 标记断开 + 留痕，并让调用方停止本轮剩余块。
            LastError = new ConnectionFailure(
                ConnectionFailureKind.Transport,
                $"从站 {slaveId} 的 {area} 区读块（起始 {block.StartAddress}，长度 {block.Length}）传输失败："
                + $"{ex.GetType().Name}: {ex.Message}");

            IsConnected = false;
            MarkBlockPointsBad(block, values);
            return false;
        }
        catch (Exception ex)
        {
            // 未知异常：**必须冒泡**（把库/代码缺陷伪装成"本块偶发读失败"是 C2 那一类失效），
            // 但**同样必须把连接标记为失效**（修复轮 2 补上，理由见方法末尾的注释）：
            // 能从这个 socket 上抛出的未知异常说明链路状态已不可信；不标记的话下一轮会放行、
            // 继续在同一条可疑 socket 上读——"标死却还在用"的同型矛盾只是换了个触发条件。
            //
            // 实测：设备消失/对端 RST 之后 NModbus 从第二次调用起恒抛 InvalidOperationException
            // （既不是传输白名单里的类型，也不是配置错误）。不在此标失效 =
            // 每一轮都抛同一个异常、永不重连、采集彻底停摆。
            //
            // 这里**不标坏点**：异常会冒泡，本轮根本不会返回 ReadResult，标了也没有消费者。
            IsConnected = false;
            LastError = new ConnectionFailure(
                ConnectionFailureKind.Unexpected,
                $"从站 {slaveId} 的 {area} 区读块（起始 {block.StartAddress}，长度 {block.Length}）抛出未预期的异常："
                + $"{ex.GetType().Name}: {ex.Message}。连接已标记失效，请重连；若反复出现请按软件缺陷排查。");

            throw;
        }
    }

    /// <summary>
    /// 传输层故障的**白名单**。
    /// </summary>
    /// <remarks>
    /// 四个成员都是"链路/连接对象坏了"的形态：
    /// <list type="bullet">
    ///   <item><see cref="TimeoutException"/>：读超时，以及本类 <see cref="ConnectAsync"/> 自己包装出的超时；</item>
    ///   <item><see cref="IOException"/>：<c>NetworkStream</c> 读写失败，含 .NET 把 socket 读超时包装成的 IOException
    ///         （**实测**：从站收包不回时 NModbus 同步读最终抛出的就是这个，见
    ///         <c>端到端_从站不回包时读超时_该块坏点且连接被标记断开</c>）；</item>
    ///   <item><see cref="SocketException"/>：socket 级失败（连接被重置等）；</item>
    ///   <item><see cref="ObjectDisposedException"/>：传输对象已被释放（与 <see cref="Dispose"/> 竞态的形态）。</item>
    /// </list>
    /// **用白名单而不是 catch-all**：多列出一种"其实不是传输故障"的异常，就等于把它伪装成坏点。
    /// 漏列的代价是异常冒泡（响亮、可归因），比静默错值/静默坏值好。
    /// </remarks>
    private static bool IsTransportFailure(Exception ex) =>
        ex is TimeoutException or IOException or SocketException or ObjectDisposedException;

    /// <summary>
    /// 位区读取（功能码 01 线圈 / 02 离散输入）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// NModbus 的 <c>ReadCoils</c>/<c>ReadInputs</c> 已经把线路上的位打包解开，返回 <c>bool[]</c>，
    /// <b>索引 i 对应地址 start + i</b>；Modbus 规范里功能码 01/02 的响应是"每 8 个线圈打包进 1 字节、
    /// LSB 对应最低地址"（NModbus 负责这一层，本类只按下标取值，不做任何加减）。
    /// </para>
    /// <para>
    /// ⚠️ <b>待现场确认</b>：以上"索引 i ↔ 地址 start+i"与"LSB 优先"是**由手写的最小 Modbus TCP 从站
    /// 按规范字节实测钉住**的（<c>ModbusTcpWireTests</c>，本机真实 socket + 真实 NModbus 客户端），
    /// 但本机<b>没有真实 Modbus 设备</b>。真实设备/独立第三方实现是否同样遵循规范，需在现场用
    /// 一个已知状态的线圈位比一次（把某个线圈强制为 1，看采集到的位是否是该地址）。
    /// 地址编号空间（0 基，规格 3.2 节）同样按此口径实现，<b>没有做任何 1 基补偿</b>。
    /// </para>
    /// </remarks>
    private static void ReadBitBlockInto(
        IModbusRequestChannel channel,
        byte slaveId,
        ModbusRegisterArea area,
        ReadBlock block,
        Dictionary<PointConfig, object?> values)
    {
        var bits = area == ModbusRegisterArea.Coil
            ? channel.ReadCoils(slaveId, (ushort)block.StartAddress, (ushort)block.Length)
            : channel.ReadInputs(slaveId, (ushort)block.StartAddress, (ushort)block.Length);

        foreach (var point in block.Points)
        {
            var index = point.Modbus!.RegisterAddress - block.StartAddress;

            values[point] = index >= 0 && index < bits.Length ? bits[index] : null;
        }
    }

    /// <summary>
    /// 寄存器区读取（功能码 03 保持寄存器 / 04 输入寄存器）。
    /// </summary>
    /// <remarks>
    /// 偏移是承重的：<c>ReadBlock.StartAddress</c> 在 Modbus 块里是**寄存器地址**，
    /// 故字节偏移必须乘 2（<c>(点地址 - 块起始) * 2</c>）。把它当成 S7 那种字节偏移用不会抛异常，
    /// 只会静默读错字节。
    /// </remarks>
    private static void ReadRegisterBlockInto(
        IModbusRequestChannel channel,
        byte slaveId,
        ModbusRegisterArea area,
        ReadBlock block,
        Dictionary<PointConfig, object?> values)
    {
        var registers = area == ModbusRegisterArea.HoldingRegister
            ? channel.ReadHoldingRegisters(slaveId, (ushort)block.StartAddress, (ushort)block.Length)
            : channel.ReadInputRegisters(slaveId, (ushort)block.StartAddress, (ushort)block.Length);

        // 寄存器序列 → 大端字节流，之后统一走 ByteDecoder（跨任务约束 3）。
        var bytes = new byte[registers.Length * 2];

        for (var i = 0; i < registers.Length; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)(registers[i] & 0xFF);
        }

        // 解码这一步可能抛的 NotSupportedException（Dtl / String）是**第二道防线**：正常路径不可达——
        // ModbusAddressValidator 已在配置期把这两个类型拦下（fail-fast、消息带点标识、且不白花一次请求）。
        // 保留解码层的这道检查，是为了拦住"绕过配置校验直接调解码"的将来代码；
        // 对应的异常分支（catch (NotSupportedException) → throw）也一并保留，绝不把它降级成坏点。
        //
        // 另注：**非 BOOL 点的 BitOffset 在这里不参与寻址**（它只在寄存器区的 BOOL 点上有意义，
        // 见类注释的已知限制）——这是刻意的宽容，不是遗漏。
        foreach (var point in block.Points)
        {
            var modbus = point.Modbus!;
            var registerByteOffset = (modbus.RegisterAddress - block.StartAddress) * 2;

            values[point] = point.DataType == PointDataType.Bool
                ? ByteDecoder.DecodeBool(
                    bytes,
                    registerByteOffset + ModbusRegisterBits.ByteIndexForBit(modbus.BitOffset),
                    modbus.BitOffset % 8)
                : ByteDecoder.DecodeNumeric(bytes, registerByteOffset, point.DataType, point.ByteOrder);
        }
    }

    private static void MarkBlockPointsBad(ReadBlock block, Dictionary<PointConfig, object?> values)
    {
        foreach (var point in block.Points)
        {
            values[point] = null;
        }
    }

    private void DisposeChannel()
    {
        _channel?.Dispose();
        _channel = null;
        IsConnected = false;
    }
}
