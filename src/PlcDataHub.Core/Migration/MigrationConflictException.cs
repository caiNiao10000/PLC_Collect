namespace PlcDataHub.Core.Migration;

/// <summary>
/// 类型冲突。规格 4.2 节：类型变更不自动执行，必须报错拦住，由人决定如何处理。
/// </summary>
public sealed class MigrationConflictException : Exception
{
    public MigrationConflictException(string message) : base(message)
    {
    }
}
