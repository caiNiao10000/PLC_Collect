using Npgsql;
using PlcDataHub.Core.Migration;

namespace PlcDataHub.Storage;

/// <summary>
/// 读取数据库中现存的数据表结构，供迁移差异引擎比对。
/// 规格 4.1 节流程的第 2 步。
/// </summary>
public sealed class SchemaIntrospector
{
    private readonly NpgsqlConnectionFactory _factory;

    /// <exception cref="ArgumentNullException">工厂为空。</exception>
    public SchemaIntrospector(NpgsqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// 用服务端的 <c>format('%I')</c> 得到一个已按需加引号、且内嵌引号已翻倍的限定标识符。
    /// </summary>
    /// <remarks>
    /// 走服务端而不是自己拼：SQL 里标识符不能用参数占位（参数只能出现在值的位置），
    /// 而手工拼引号是内省侧唯一一处"引号里的内容不是本项目产出的"，最易出错。
    /// <c>%I</c> 的语义与 PostgreSQL 的 <c>quote_ident()</c> 相同：安全字符集内不加引号，
    /// 其余加双引号并把内部的 <c>"</c> 翻倍。
    /// </remarks>
    private static async Task<string> GetQuotedIdentifierAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // 参数名一律加 p_ 前缀：@schema / @table 本身能正常工作（实测 Npgsql 8.0.9），
        // 但 schema 是 PostgreSQL 的保留字、table 与 pg_class.relname 这类列名极易混淆，
        // 加前缀可避免读代码时把占位符当成列引用。
        command.CommandText = $"select format('%I.%I', @p_schema, @p_table)";
        command.Parameters.AddWithValue("p_schema", MigrationPlanner.DataSchema);
        command.Parameters.AddWithValue("p_table", tableName);
        // format() 在参数非 NULL 时必然返回一行一列的非 NULL 文本。
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// 读取 schema d 下所有表的列与行数。
    /// d schema 不存在时返回空集合——这表示"还没建过任何数据表"，不是错误。
    /// </summary>
    /// <remarks>
    /// 表名用 <see cref="StringComparer.Ordinal"/> 索引：
    /// schema d 下不该出现仅大小写不同的两张表（那种结构不明确的情况由
    /// <see cref="MigrationPlanner"/> 负责响亮拒绝），此处不做归一。
    /// </remarks>
    public async Task<IReadOnlyList<ExistingTable>> ReadDataSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);

        var columnsByTable = new Dictionary<string, List<ExistingColumn>>(StringComparer.Ordinal);

        // 一次查出所有列，而不是每张表查一次：
        // schema d 不存在时这个查询返回 0 行（不是报错），于是"没建过表"与"表没有列"
        // 都不需要特判——connection 能开起来就说明库本身是通的，schema 缺不缺由行数表达。
        await using (var columnsCommand = connection.CreateCommand())
        {
            // data_type 返回的是 SQL 标准名（int4 → integer、float8 → double precision），
            // 与 SqlTypeMapper 产出的名字在 MigrationPlanner.TypesEquivalent 里归一后可比。
            // 只认 BASE TABLE：视图与外部表没有可以 ALTER 的本地结构。
            columnsCommand.CommandText = """
                select c.table_name, c.column_name, c.data_type
                from information_schema.columns c
                join information_schema.tables t
                  on t.table_schema = c.table_schema and t.table_name = c.table_name
                where c.table_schema = @p_schema
                  and t.table_type = 'BASE TABLE'
                order by c.table_name, c.ordinal_position
                """;
            columnsCommand.Parameters.AddWithValue("p_schema", MigrationPlanner.DataSchema);

            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tableName = reader.GetString(0);
                if (!columnsByTable.TryGetValue(tableName, out var columns))
                {
                    columns = [];
                    columnsByTable[tableName] = columns;
                }

                columns.Add(new ExistingColumn(reader.GetString(1), reader.GetString(2)));
            }
        }

        if (columnsByTable.Count == 0)
        {
            return Array.Empty<ExistingTable>();
        }

        // 行数单独查。必须用 count(*) 精确统计：
        // pg_class.reltuples 在表刚建、尚未 ANALYZE 时返回 -1（实测 PG 18.6：建表后、
        // insert 7 行后都仍是 -1，只有 ANALYZE 之后才变成 7），而迁移预览要报给用户
        // "将丢弃 N 行"的真实数字，-1 行是没有意义的显示。
        // 表数量在 5~15 张量级，逐表 count(*) 的开销可接受。
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var tableName in columnsByTable.Keys)
        {
            await using var countCommand = connection.CreateCommand();

            // ⚠️ 表名来自 information_schema（第三方输入）。这里让**服务端**做标识符
            // 引用与转义，而不是自己拼引号：SQL 里标识符不能用参数占位（PG 只允许参数
            // 出现在值的位置），但 format('%I') 会在服务端按需加双引号并把内嵌的 "
            // 翻倍，效果与手工拼一致而少一处易错的手写转义。
            // 手工加引号的政策在 MigrationPlanner.Qualified 已经有一份，不必在内省侧复制。
            var quotedTable = await GetQuotedIdentifierAsync(connection, tableName, cancellationToken);

            countCommand.CommandText = $"select count(*) from {quotedTable}";

            // count(*) 是 bigint，Npgsql 映射为 long。
            var scalar = await countCommand.ExecuteScalarAsync(cancellationToken);
            rowCounts[tableName] = scalar is long value ? value : 0L;
        }

        // 保序：按表名（Ordinal）返回，让结果与访问顺序无关，
        // 迁移计划的步骤顺序对同一库状态稳定可比。
        return columnsByTable
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ExistingTable(
                kv.Key,
                kv.Value,
                rowCounts.TryGetValue(kv.Key, out var count) ? count : 0))
            .ToList();
    }
}
