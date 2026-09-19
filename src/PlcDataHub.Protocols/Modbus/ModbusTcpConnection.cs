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
///         <description><b>在发出任何请求之前</b>对全部点校验并直接抛出（见 <see cref="EnsurePointAddressable"/>），
///         使"这个配置永远不可能工作"不至于被伪装成"本周期读失败"。</description></item>
///   <item><term><see cref="SlaveException"/></term>
///         <description>从站**答复了**异常码（如非法数据地址）：链路是好的，坏的是这个块的请求。
///         该块的点写 NULL，<b>连接保持 IsConnected = true</b>——否则采集器会每周期无谓重连一次。</description></item>
///   <item><term>其它异常（超时/连接被重置/帧错误…）</term>
///         <description>该块的点写 NULL、<b>IsConnected 置 false</b>（交给重连逻辑），同一轮的其它块照常尝试。</description></item>
/// </list>
/// 取消（<see cref="OperationCanceledException"/>）与上述三者都不同：<b>一律冒泡</b>，它不是"这个点本轮坏了"。
/// </para>
/// <para>
/// <b>返回的是原始值</b>：未乘 Scale、未加 Offset（规格 3.4 节要求工程值转换在落库前完成）。
/// </para>
/// </remarks>
public sealed class ModbusTcpConnection : IPlcConnection
{
    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法下界。0 是广播地址，不能用于读取。</summary>
    public const int MinSlaveId = 1;

    /// <summary>规格 3.2 节 <c>mb_slave</c> 的合法上界。248~255 保留。</summary>
    public const int MaxSlaveId = 247;

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

    /// <summary>
    /// 建立连接。失败抛异常（含超时抛 <see cref="TimeoutException"/>），由采集器的重连逻辑处理。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_injectedChannel is not null)
        {
            // 测试注入路径：不建 socket，也不在这里释放注入的通道——它的生命周期归测试所有，
            // 在此释放会让"重复 ConnectAsync"把测试桩一起释放掉。
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
            throw new TimeoutException(
                $"连接 {_connection.Host}:{_connection.Port} 超时（{_options.TimeoutMs} ms）");
        }
        catch
        {
            // 连接失败（拒绝/不可达/调用方取消）也要把 socket 放掉，否则句柄泄漏。
            client.Dispose();
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
    /// <exception cref="InvalidOperationException">尚未连接。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 某个点的从站号不在 1~247、寄存器地址越界、或位偏移越界（见 <see cref="EnsurePointAddressable"/>）。
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// 某个点的类型与寄存器区组合无法读取（位区里的非 BOOL 点），或该点类型本期不支持解码
    /// （<see cref="PointDataType.Dtl"/> / <see cref="PointDataType.String"/>，由 <see cref="ByteDecoder"/> 抛出）。
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

            EnsurePointAddressable(point);
            modbusPoints.Add(point);
        }

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
                ReadBlockInto(channel, slaveId, area, block, values);
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
    /// 地址与类型校验。**在发出任何请求之前**对全部 Modbus 点跑一遍，任何一条不满足都响亮抛出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每一条规则都对应一条真实的静默失效路径（不校验的后果都是"读到错值/永远坏值且没有信号"）：
    /// <list type="number">
    ///   <item><b>从站号越界</b>（C4）：<c>(byte)slaveId</c> 直接截断时，0（广播）与 248~255
    ///         会被编码成一个看起来合法的请求字节，从站不会拒绝——于是读到别处的数据而不报错。</item>
    ///   <item><b>寄存器地址越界</b>：地址要经 <c>(ushort)</c> 落到请求里，超出 0~65535 会被截断，
    ///         请求打到完全不相干的寄存器上。</item>
    ///   <item><b>点的末寄存器越界</b>：地址合法但"地址 + 宽度 - 1"越过 65535 时，
    ///         块尾回绕，最后一个点读到块首附近的数据。</item>
    ///   <item><b>寄存器区未定义</b>：枚举被强转成未定义值时，功能码选择会落到某个默认分支。</item>
    ///   <item><b>位区里放了非 BOOL 点</b>：线圈/离散输入每个地址只有一位，WORD 在这里无法表达；
    ///         若按寄存器路径去读，会拿线圈的地址空间去发功能码 03/04——那是另一个编号空间。</item>
    ///   <item><b>位区里的 BitOffset 不为 0</b>：位区本身已按位寻址，位偏移无处可用；
    ///         静默忽略它等于"配置说取第 3 位、实际取了第 0 位"。</item>
    ///   <item><b>寄存器区 BOOL 的 BitOffset 越界</b>：不在 0~15 时位提取返回 null，
    ///         该点每个周期都是坏值且没有任何错误信号。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 异常分两类，Plan 2 的采集器若要按点隔离配置错误，catch 这两个类型即可：
    /// <see cref="ArgumentOutOfRangeException"/>（取值问题，<c>ParamName</c> 一律是 <c>points</c>，
    /// 与规划器对"点宽度超过上限"的处置同型）与 <see cref="NotSupportedException"/>
    /// （类型组合不支持，与 <see cref="ByteDecoder"/> 对 Dtl/String 的处置同型）。
    /// </para>
    /// </remarks>
    private static void EnsurePointAddressable(PointConfig point)
    {
        var modbus = point.Modbus!;
        var where = $"采集点 {point.PointCode}（{point.PointName}）";

        if (modbus.SlaveId is < MinSlaveId or > MaxSlaveId)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.SlaveId,
                $"{where} 的从站号 {modbus.SlaveId} 超出 {MinSlaveId}~{MaxSlaveId}（规格 3.2 节 mb_slave）。"
                + "0 是广播地址、248~255 保留，它们会被编码成一个看起来合法的请求字节，"
                + "于是读到别的从站的数据而不会报错。请把从站号改为 1~247。");
        }

        switch (modbus.Area)
        {
            case ModbusRegisterArea.Coil:
            case ModbusRegisterArea.DiscreteInput:
                if (point.DataType != PointDataType.Bool)
                {
                    throw new NotSupportedException(
                        $"{where} 位于 {modbus.Area}（位区），每个地址只有一位，无法表达数据类型 {point.DataType}。"
                        + "位区只能配置 Bool 点；若该点确实是 16 位量，请把它改到保持寄存器/输入寄存器区。");
                }

                if (modbus.BitOffset != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        "points", modbus.BitOffset,
                        $"{where} 位于 {modbus.Area}（位区），按位寻址——一个线圈/离散输入就是一位，"
                        + $"BitOffset 必须为 0，实际为 {modbus.BitOffset}。静默忽略它会让'配置说取第 {modbus.BitOffset} 位、"
                        + "实际取了该地址本身'。");
                }

                break;

            case ModbusRegisterArea.HoldingRegister:
            case ModbusRegisterArea.InputRegister:
                if (point.DataType == PointDataType.Bool && modbus.BitOffset is < 0 or > 15)
                {
                    throw new ArgumentOutOfRangeException(
                        "points", modbus.BitOffset,
                        $"{where} 是 {modbus.Area} 区的 Bool 点，寄存器内的位偏移必须在 0~15，实际为 {modbus.BitOffset}。"
                        + "越界的位偏移取不出任何位（该点会每周期写坏值）。");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(
                    "points", modbus.Area,
                    $"{where} 的寄存器区 {modbus.Area}（{(int)modbus.Area}）不是已定义的值，无法确定读取用的功能码。");
        }

        if (modbus.RegisterAddress < 0)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.RegisterAddress,
                $"{where} 的地址 {modbus.RegisterAddress} 为负。Modbus 地址是 0 基的无符号 16 位量，"
                + "负值被转成 ushort 后会打到完全不相干的地址上。");
        }

        var width = ReadBlockPlanner.RegisterWidth(point);
        var lastRegister = modbus.RegisterAddress + width - 1;

        if (lastRegister > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                "points", modbus.RegisterAddress,
                $"{where} 的地址 {modbus.RegisterAddress} 加上它占用的 {width} 个寄存器后，末寄存器地址 {lastRegister} "
                + $"越过 Modbus 地址上限 {ushort.MaxValue}，请求会回绕到块首附近，静默读到错的数据。");
        }
    }

    /// <summary>
    /// 读一个块并按区走对应的物理路径；逐块做故障隔离。
    /// </summary>
    private void ReadBlockInto(
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
                    // 已由 EnsurePointAddressable 拦下。这里再抛一次，是为了将来给 ModbusRegisterArea
                    // 加新枚举值时不会"默默用错功能码去读"——而它会被下面的 NotSupportedException
                    // 分支原样冒泡，绝不会变成 NULL。
                    throw new NotSupportedException(
                        $"未知的寄存器区 {area}（{(int)area}），无法确定读取用的功能码。");
            }
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
            throw;
        }
        catch (SlaveException)
        {
            // 从站答复了异常码（例如非法数据地址 0x02）：设备与链路都是好的，坏的是这个块的请求。
            // 标记整条连接断开会让采集器每周期做一次无谓的重连。
            MarkBlockPointsBad(block, values);
        }
        catch (Exception)
        {
            // 传输层故障（超时/连接被重置/帧错误…）：链路已不可用 → 标记断开，交给重连逻辑。
            // 同一轮的其它块照常尝试：单块失败只影响该块（规格 5.3 节）。
            IsConnected = false;
            MarkBlockPointsBad(block, values);
        }
    }

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
