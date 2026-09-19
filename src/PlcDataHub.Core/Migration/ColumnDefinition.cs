namespace PlcDataHub.Core.Migration;

/// <summary>数据库中现存的列。类型取 information_schema.columns.data_type 的原始值。</summary>
public sealed record ExistingColumn(string Name, string PostgresType);

/// <summary>数据库中现存的表。</summary>
/// <param name="TableName">不含 schema 的表名</param>
/// <param name="Columns">现存列</param>
/// <param name="RowCount">行数，用于在破坏性操作提示里报出会丢多少行</param>
public sealed record ExistingTable(string TableName, IReadOnlyList<ExistingColumn> Columns, long RowCount);
