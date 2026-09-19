namespace PlcDataHub.Core.Migration;

/// <summary>迁移动作的种类。</summary>
public enum MigrationStepKind
{
    CreateTable,
    CreateIndex,
    AddColumn,
    RenameColumn,
    SoftDeleteColumn,
    RebuildColumn,
    DropColumn,
    DropTable,
}

/// <summary>
/// 一条迁移动作。Sql 是最终执行的语句；Description 是给人在预览弹窗里看的说明。
/// </summary>
/// <param name="Kind">动作种类</param>
/// <param name="TableName">目标表（不含 schema）</param>
/// <param name="Sql">要执行的 SQL</param>
/// <param name="IsDestructive">是否会不可逆地丢数据</param>
/// <param name="Description">人类可读说明，破坏性动作必须写清后果</param>
public sealed record MigrationStep(
    MigrationStepKind Kind,
    string TableName,
    string Sql,
    bool IsDestructive,
    string Description);
