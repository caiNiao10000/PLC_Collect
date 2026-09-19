namespace PlcDataHub.Core.Model;

/// <summary>期望表中的一列。</summary>
/// <param name="Name">列名（ASCII）</param>
/// <param name="PostgresType">PostgreSQL 类型</param>
/// <param name="IsNullable">是否允许 NULL</param>
public sealed record DesiredColumn(string Name, string PostgresType, bool IsNullable);

/// <summary>期望的表结构。TableName 不含 schema，固定位于 schema d 下。</summary>
public sealed record DesiredTable(string TableName, IReadOnlyList<DesiredColumn> Columns);

/// <summary>由配置推算出的全部期望结构。</summary>
public sealed record DesiredSchema(IReadOnlyList<DesiredTable> Tables)
{
    /// <summary>固定列：时间戳 ts。</summary>
    public const string TimestampColumn = "ts";

    /// <summary>固定列：行质量 q。0=好 1=部分坏点 2=补写。</summary>
    public const string QualityColumn = "q";

    /// <summary>固定列：原始采集时刻 src_ts，仅补写行非空。</summary>
    public const string SourceTimestampColumn = "src_ts";
}
