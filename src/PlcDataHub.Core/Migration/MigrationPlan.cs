namespace PlcDataHub.Core.Migration;

/// <summary>
/// 迁移差异计划。规格 4.1 节：先生成计划返回预览，用户确认后才执行。
/// </summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationStep> Steps)
{
    /// <summary>空计划表示配置与库结构一致，不需要任何 DDL。这是幂等性的判定依据。</summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>会丢数据的动作。预览界面必须把这些高亮出来。</summary>
    public IReadOnlyList<MigrationStep> DestructiveSteps =>
        Steps.Where(s => s.IsDestructive).ToList();
}

/// <summary>迁移选项。默认值是"安全优先"：软删除、不删表。</summary>
/// <param name="SoftDeleteRemovedColumns">删除采集点时改名为 deleted_* 保留数据，而不是 DROP。默认 true</param>
/// <param name="DropRemovedTables">采集组被删除时是否连表一起删。默认 false</param>
public sealed record MigrationOptions(
    bool SoftDeleteRemovedColumns = true,
    bool DropRemovedTables = false);
