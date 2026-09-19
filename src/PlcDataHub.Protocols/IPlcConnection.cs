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
/// 协议抽象。规格 5.1 节：一条连接由一个线程独占，绝不并发访问同一实例。
/// </summary>
public interface IPlcConnection : IDisposable
{
    /// <summary>建立连接。失败抛异常，由采集器的重连逻辑处理。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>读取一组点。返回的字典必须为每个传入的点都给出条目，失败的点值为 null。</summary>
    Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken);

    /// <summary>当前是否处于已连接状态。</summary>
    bool IsConnected { get; }
}
