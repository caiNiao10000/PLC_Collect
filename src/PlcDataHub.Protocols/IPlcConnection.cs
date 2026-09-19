using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 一次读请求覆盖的地址范围。
/// Points 里所有点的地址都落在 [StartAddress, StartAddress + Length) 内。
/// </summary>
/// <param name="StartAddress">
/// ⚠️ **双重语义，务必按调用方身份解读**：
/// <list type="bullet">
///   <item>由 <see cref="ReadBlockPlanner.PlanForModbus"/> 产出时，它是 **Modbus 寄存器地址**
///         （16 位寄存器编号，0 基），<paramref name="Length"/> 的单位是**寄存器数**。</item>
///   <item>S7 侧的块（后续任务实现）里，它是 **S7 字节偏移**，<paramref name="Length"/> 的单位是
///         **字节数**。</item>
/// </list>
/// 同一 record 承载两种单位是刻意保留的（Modbus 与 S7 的定位逻辑本就不同），
/// 但它是个真实陷阱：**实现 S7 连接时若把 Modbus 块按字节偏移处理，或反之，
/// 地址会静默算错——不抛异常、不返回 null，只是读到错的字节。**
/// 判别依据只有一个：这个块是哪个协议规划器产出的。本任务未引入协议标签，
/// 若后续任务觉得该判别不可靠，应在此 record 上加显式的协议/单位字段，而不是靠约定。
/// </param>
/// <param name="Length">覆盖长度。单位随 <paramref name="StartAddress"/> 的语义而定（寄存器数或字节数）。</param>
/// <param name="Points">本次请求要解码的点，全部落在覆盖范围内。</param>
public sealed record ReadBlock(int StartAddress, int Length, IReadOnlyList<PointConfig> Points);

/// <summary>一次采集回合的结果：点 → 值。值为 null 表示该点本次读取失败（坏点）。</summary>
public sealed record ReadResult(IReadOnlyDictionary<PointConfig, object?> Values);

/// <summary>
/// 失败的类别。存在的理由是**让"点一直坏值"可归因**：
/// 两类失败的处置完全相反——一类要改配置，一类要等链路/重连。
/// </summary>
public enum ConnectionFailureKind
{
    /// <summary>
    /// 从站/设备**答复了拒绝**（Modbus 的异常码，如 01 不支持该功能码、02 地址超设备范围）。
    /// 链路是好的，坏的是请求或地址配置：**不该重连**，该检查配置。
    /// 这类失败在规格 5.3 节下被降级为坏点，因此必须由 <see cref="IPlcConnection.LastError"/> 留痕，
    /// 否则它与"偶发读失败"完全不可区分。
    /// </summary>
    SlaveRejected,

    /// <summary>
    /// 传输层故障（超时、连接被重置、读写失败、传输对象已释放）。链路不可用：**该重连**。
    /// </summary>
    Transport,

    /// <summary>
    /// **未预期的异常**：不属于传输类白名单，也不是配置错误——即"链路坏了"与"配置错了"都解释不了它
    /// （典型来源是库的内部状态：设备消失/对端 RST 之后，NModbus 从第二次调用起恒抛
    /// <see cref="InvalidOperationException"/>）。
    /// <para>
    /// 处置与 <see cref="Transport"/> 相同：**该重连**。理由：能从一个 socket 上抛出未知异常，
    /// 说明这条连接的状态已不可信；不重连就会"每轮都抛同一个异常、永不恢复"。
    /// 与 <see cref="Transport"/> 分开是为了让运行状态能区分"链路坏（现场问题）"与
    /// "库/代码缺陷（软件问题）"——两者的排查方向完全不同。
    /// </para>
    /// </summary>
    Unexpected,
}

/// <summary>最近一次失败的摘要：类别 + 可读消息（消息里应包含点标识或从站/地址等定位信息）。</summary>
/// <param name="Kind">失败类别，决定上层该"改配置"还是"重连"。</param>
/// <param name="Message">失败消息。必须能回答"哪里错了"，供 Plan 2 写入 <c>rt.group_status</c>。</param>
public sealed record ConnectionFailure(ConnectionFailureKind Kind, string Message);

/// <summary>
/// 协议抽象。规格 5.1 节：一条连接由一个线程独占，绝不并发访问同一实例。
/// </summary>
/// <remarks>
/// <para>
/// <b>失败与重连的契约（跨任务约定，Plan 2 的采集器按此实现）</b>：
/// <list type="number">
///   <item><see cref="ConnectAsync"/> 失败抛异常，由采集器的重连逻辑处理；失败也会写入 <see cref="LastError"/>。</item>
///   <item><see cref="ReadAsync"/> 单轮内的**块级**失败不抛异常：该块的点值为 null，其余块照常；失败写入 <see cref="LastError"/>。</item>
///   <item>传输类故障（<see cref="ConnectionFailureKind.Transport"/>）与**未预期的异常**
///         （<see cref="ConnectionFailureKind.Unexpected"/>）都会把 <see cref="IsConnected"/> 置 false，
///         此后 <see cref="ReadAsync"/> **抛 <see cref="InvalidOperationException"/>**（"尚未连接"），
///         采集器必须重新 <see cref="ConnectAsync"/> 才能继续——**不要**在一个已判定失效的连接上继续读。</item>
///   <item>配置错误（不支持的数据类型、非法地址、从站号越界等，异常类型是 <see cref="ArgumentException"/>
///         家族或 <see cref="NotSupportedException"/>）**抛异常但不置 false、也不写 LastError**：
///         它们在使用前就暴露，**异常本身就是信号**（消息里带点标识），连接状态并没有因此不可信。</item>
/// </list>
/// </para>
/// </remarks>
public interface IPlcConnection : IDisposable
{
    /// <summary>建立连接。失败抛异常，由采集器的重连逻辑处理。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>读取一组点。返回的字典必须为每个传入的点都给出条目，失败的点值为 null。</summary>
    Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken);

    /// <summary>当前是否处于已连接状态。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 最近一次失败的摘要；从未失败过时为 null。
    /// <para>
    /// **语义是"最近一次"（粘性），成功的一轮不会清空它**：清空会让"上周期出过错"在运行状态里消失，
    /// 而操作员要看的正是"有没有出过错、错在哪一类"。要判断"此刻是否正常"请看 <see cref="IsConnected"/>
    /// 与本轮的返回值。
    /// </para>
    /// <para>
    /// **哪些失败会写入本属性**（与上面的失败契约逐条对应）：
    /// 不抛异常的失败（从站拒绝 <see cref="ConnectionFailureKind.SlaveRejected"/>、
    /// 传输故障 <see cref="ConnectionFailureKind.Transport"/>）**必须**写入；
    /// 未预期的异常（<see cref="ConnectionFailureKind.Unexpected"/>）在冒泡前写入；
    /// <b>配置错误**不写**</b>——它抛异常、连接状态并未因此不可信，异常本身就是信号。
    /// </para>
    /// </summary>
    ConnectionFailure? LastError { get; }
}
