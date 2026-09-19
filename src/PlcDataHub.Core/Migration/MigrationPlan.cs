namespace PlcDataHub.Core.Migration;

/// <summary>
/// 迁移差异计划。规格 4.1 节：先生成计划返回预览，用户确认后才执行。
/// </summary>
/// <param name="Steps">迁移动作。构造时会被包成只读集合，调用方无法改写。</param>
public sealed record MigrationPlan(IReadOnlyList<MigrationStep> Steps)
{
    /// <summary>
    /// 迁移动作。构造时包一层 <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>。
    /// </summary>
    /// <remarks>
    /// 声明类型是 <see cref="IReadOnlyList{T}"/>，但那只挡住"换一个 list"，
    /// 挡不住调用方把它转回 <see cref="List{T}"/> 再 <c>Add</c>/<c>Clear</c>；
    /// 一旦被改写，<see cref="IsEmpty"/> 与 <see cref="DestructiveSteps"/> 就会与
    /// "用户已复核过的计划内容"自相矛盾（预览时为空、执行时非空，或反之）。
    /// 计划是"给人复核过的凭据"，不该可变。
    /// </remarks>
    public IReadOnlyList<MigrationStep> Steps { get; init; } =
        new System.Collections.ObjectModel.ReadOnlyCollection<MigrationStep>(Steps.ToList());

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
