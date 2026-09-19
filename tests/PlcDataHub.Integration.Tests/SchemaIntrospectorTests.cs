using FluentAssertions;
using PlcDataHub.Storage;
using Xunit;

namespace PlcDataHub.Integration.Tests;

/// <summary>
/// <see cref="SchemaIntrospector"/> 对真实 PostgreSQL 的行为。
/// 没有 PLCDATAHUB_TEST_PG 时全部跳过——每个用例的第一句都是 Skip.IfNot。
/// </summary>
public class SchemaIntrospectorTests
{
    [SkippableFact]
    public async Task 能读出_d_schema_下的表与列及行数()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await db.ResetAsync();

        await using (var connection = await db.OpenAsync())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                create table d.plc01_fast (
                    ts timestamp not null,
                    q smallint not null,
                    wen_du double precision
                );
                insert into d.plc01_fast (ts, q, wen_du)
                select now() - (i || ' seconds')::interval, 0, i::double precision
                from generate_series(1, 7) as i;
                """;
            await create.ExecuteNonQueryAsync();
        }

        var factory = new NpgsqlConnectionFactory(db.ConnectionString);
        var introspector = new SchemaIntrospector(factory);

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        var table = tables.Should().ContainSingle().Subject;
        table.TableName.Should().Be("plc01_fast");
        table.RowCount.Should().Be(7);
        table.Columns.Select(c => c.Name).Should().BeEquivalentTo("ts", "q", "wen_du");

        // 期望值 "double precision" 是实测的 information_schema.columns.data_type 原始值
        // （PG 的 internal 名是 float8，data_type 返回的是 SQL 标准名）。
        // 内省读到的类型要与配置侧 SqlTypeMapper 产出的类型名**同档**，
        // 否则 MigrationPlanner.TypesEquivalent 归一后仍会误判为类型冲突。
        table.Columns.Single(c => c.Name == "wen_du").PostgresType.Should().Be("double precision");
    }

    [SkippableFact]
    public async Task 没有_d_schema_时返回空而不是抛异常()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();

        // ⚠️ 必须先 ResetAsync 再 drop d，不能依赖"上一条用例刚建过 d"：
        // xunit 不保证用例执行顺序，单独跑这一条时 d schema 可能本来就不存在，
        // 那样这条用例就变成在测"库恰好是空的"而不是在测"没有 d schema 时内省返回空"。
        // ResetAsync 保证三个 schema 都存在，下面的 drop 才是本条用例自己制造的差异。
        await db.ResetAsync();

        await using (var connection = await db.OpenAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = "drop schema if exists d cascade;";
            await drop.ExecuteNonQueryAsync();
        }

        var introspector = new SchemaIntrospector(new NpgsqlConnectionFactory(db.ConnectionString));

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        tables.Should().BeEmpty("d schema 不存在时应视为没有任何数据表，交给迁移器去建");
    }

    /// <summary>
    /// 行数必须是精确值，即使表刚建、从未 ANALYZE。
    /// <para>
    /// 这是 <c>count(*)</c> 而非 <c>pg_class.reltuples</c> 存在的**唯一理由**：
    /// reltuples 在表从未被 ANALYZE / VACUUM 时是 -1（不是 0）。
    /// 迁移预览要报给用户"将丢弃 N 行"，显示 -1 行是没有意义的。
    /// 本用例先证明"统计信息确实还是不可用的 -1"，再证明我们报的仍是精确的 7 ——
    /// 少了前半句，用例只说明了"读到 7"，说明不了"为什么不能用更快的 reltuples"。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task 行数取精确值而不是未_ANALYZE_的统计估值()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await db.ResetAsync();

        await using (var connection = await db.OpenAsync())
        {
            await using var create = connection.CreateCommand();

            // storage_parameter 关掉本表的 autovacuum：默认 autovacuum_naptime=60s，
            // 一个"刚好没赶上 autovacuum"的用例是时序相关的、会偶发失败的。
            // 关掉之后"从未 ANALYZE 过"由表的定义保证，与跑得多快无关。
            // ⚠️ 这不改变 PG 对 reltuples 的语义，只是让本用例不受后台进程干扰。
            create.CommandText = """
                create table d.plc01_fast (
                    ts timestamp not null,
                    q smallint not null,
                    wen_du double precision
                ) with (autovacuum_enabled = false);
                insert into d.plc01_fast (ts, q, wen_du)
                select now() - (i || ' seconds')::interval, 0, i::double precision
                from generate_series(1, 7) as i;
                """;
            await create.ExecuteNonQueryAsync();
        }

        var introspector = new SchemaIntrospector(new NpgsqlConnectionFactory(db.ConnectionString));

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        tables.Should().ContainSingle().Which.RowCount.Should().Be(
            7, "count(*) 是精确值：7 行就是 7 行，与本表有没有被 ANALYZE 过无关");

        // 对照组：直接查 pg_class，证明"更快的那个做法"此刻给出的是 -1。
        // 如果哪天有人把实现改回 reltuples，上面的断言会变成 RowCount == -1 而失败，
        // 本查询则解释了它为什么会错。
        await using var verify = await db.OpenAsync();
        await using var reltuplesCommand = verify.CreateCommand();
        reltuplesCommand.CommandText = """
            select c.reltuples::bigint
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'd' and c.relname = 'plc01_fast'
            """;

        var reltuples = (long)(await reltuplesCommand.ExecuteScalarAsync())!;

        reltuples.Should().Be(
            -1, "从未 ANALYZE 过的表 reltuples 是 -1（不是 0），正是不能用它做行数预览的原因");

        // 真实行数（与内省结果无关地再数一遍）：证明上一条 -1 对应的真实值是 7。
        await using var countCommand = verify.CreateCommand();
        countCommand.CommandText = "select count(*) from d.plc01_fast";
        var realCount = (long)(await countCommand.ExecuteScalarAsync())!;
        realCount.Should().Be(7);
    }

    /// <summary>
    /// 多张表：有行的与没行的都要被返回，且空表行数必须是 0（而不是"缺席"或 -1）。
    /// </summary>
    [SkippableFact]
    public async Task 多张表时有行的与空表都返回且空表行数为零()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await db.ResetAsync();

        await using (var connection = await db.OpenAsync())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                create table d.plc01_fast (ts timestamp not null, q smallint not null);
                create table d.plc02_slow (ts timestamp not null, q smallint not null);
                insert into d.plc01_fast (ts, q)
                select now() - (i || ' seconds')::interval, 0
                from generate_series(1, 3) as i;
                """;
            await create.ExecuteNonQueryAsync();
        }

        var introspector = new SchemaIntrospector(new NpgsqlConnectionFactory(db.ConnectionString));

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        tables.Select(t => t.TableName).Should().BeEquivalentTo("plc01_fast", "plc02_slow");

        // 空表必须在结果里，且行数是 0。
        // "库里没有这张表"与"这张表有 0 行"对迁移器是两件完全不同的事：
        // 前者要 CREATE TABLE，后者什么都不用做。把空表漏掉会让每次启动都重复建表。
        tables.Single(t => t.TableName == "plc02_slow").RowCount.Should().Be(0);
        tables.Single(t => t.TableName == "plc01_fast").RowCount.Should().Be(3);

        // 两张表的列都要完整读到（不是只读了"第一张表的列"）
        tables.Single(t => t.TableName == "plc01_fast").Columns
            .Select(c => c.Name).Should().BeEquivalentTo("ts", "q");
        tables.Single(t => t.TableName == "plc02_slow").Columns
            .Select(c => c.Name).Should().BeEquivalentTo("ts", "q");
    }

    /// <summary>
    /// 表名不是"我们自己产出的安全标识符"时也要能读出行数。
    /// <para>
    /// 行数查询必须把表名当**标识符**拼进 SQL（PG 不允许参数占位标识符），
    /// 而表名是从 information_schema 读来的第三方输入。这条用例用一个含
    /// 空格与双引号的表名把这个拼接面钉住：转义写错的话，生成的 SQL 要么语法错、
    /// 要么指向另一张表，两种都会让本用例失败。
    /// </para>
    /// <para>
    /// 注意本用例**不覆盖** SQL 注入的完整面（那需要更系统的字符集遍历），
    /// 只覆盖"引号必须被正确转义"这一个最容易写错的点。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task 表名含空格与双引号时也能正确读出行数()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await db.ResetAsync();

        // 建表时标识符里的 " 用 "" 表示（SQL 的引号转义规则）
        await using (var connection = await db.OpenAsync())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                create table d."we""ird table" (ts timestamp not null, q smallint not null);
                insert into d."we""ird table" (ts, q)
                select now(), 0 from generate_series(1, 2);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var introspector = new SchemaIntrospector(new NpgsqlConnectionFactory(db.ConnectionString));

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        var table = tables.Should().ContainSingle().Subject;
        table.TableName.Should().Be("we\"ird table", "表名要原样返回，不能被转义或截断");
        table.RowCount.Should().Be(2, "含引号的表名若不按标识符转义，这条 count(*) 会报语法错");
    }
}
