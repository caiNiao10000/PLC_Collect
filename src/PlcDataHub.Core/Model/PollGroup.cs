namespace PlcDataHub.Core.Model;

/// <summary>
/// 采集组。规格 3.2 节 cfg.poll_group 的内存表示。
/// 一组对应数据库中一张宽表。
/// </summary>
/// <param name="GroupId">组主键</param>
/// <param name="ConnId">所属连接</param>
/// <param name="GroupCode">组代码，仅 [a-z0-9_]</param>
/// <param name="GroupName">显示名，可中文</param>
/// <param name="PeriodMs">采集周期（毫秒），1000 ~ 600000</param>
/// <param name="TableName">目标表名（不含 schema），位于 schema d 下</param>
/// <param name="Enabled">是否启用</param>
/// <param name="Points">组内采集点</param>
public sealed record PollGroup(
    int GroupId,
    int ConnId,
    string GroupCode,
    string GroupName,
    int PeriodMs,
    string TableName,
    bool Enabled,
    IReadOnlyList<PointConfig> Points);
