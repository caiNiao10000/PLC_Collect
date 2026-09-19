using Npgsql;

namespace PlcDataHub.Integration.Tests;

/// <summary>
/// 集成测试用的真实 PostgreSQL。
/// 环境变量 PLCDATAHUB_TEST_PG 未设置时 <see cref="IsAvailable"/> 为 false，
/// 相关测试会跳过而不是失败——这样没有数据库的机器也能跑通 `dotnet test`。
/// 需要真实库时设置：
/// <c>$env:PLCDATAHUB_TEST_PG = "Host=localhost;Port=5432;Database=plcdatahub_test;Username=postgres;Password=***"</c>
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private const string EnvVarName = "PLCDATAHUB_TEST_PG";

    private readonly string _connectionString;

    public TestDatabase()
    {
        _connectionString = Environment.GetEnvironmentVariable(EnvVarName)
                            ?? string.Empty;
    }

    /// <summary>本机是否配置了测试数据库。</summary>
    /// <remarks>
    /// xunit 的 <c>[Fact]</c> 的 <c>Skip</c> 参数是编译期常量，无法表达"运行时才知道有没有库"，
    /// 故集成测试一律用 <c>[SkippableFact]</c> + <c>Skip.IfNot(TestDatabase.IsAvailable, ...)</c>。
    /// 判定放在属性上（而不是环境变量读取处）是为了让"有没有库"只有一处真相。
    /// </remarks>
    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName));

    public string ConnectionString => _connectionString;

    /// <summary>打开一条到测试库的连接。未配置环境变量时抛错——调用方应先查 <see cref="IsAvailable"/>。</summary>
    /// <exception cref="InvalidOperationException">未设置环境变量。</exception>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"未设置环境变量 {EnvVarName}，无法执行集成测试");
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>清空 data schema 下的所有表，让每个用例从干净状态开始。</summary>
    /// <remarks>
    /// ⚠️ 每个用例**开头**都要调它，不要依赖"上一条用例刚建过某个 schema"。
    /// xunit 不保证同一个类里的用例按源码顺序执行，单独跑一条也必须成立；
    /// 反之，留下状态的用例会污染后续用例。这里的做法是每次都把三个 schema
    /// 推倒重建，于是"有没有 d schema"由本条用例自己决定，而不是由执行顺序决定。
    /// </remarks>
    public async Task ResetAsync()
    {
        await using var connection = await OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            drop schema if exists d cascade;
            drop schema if exists cfg cascade;
            drop schema if exists rt cascade;
            create schema d;
            create schema cfg;
            create schema rt;
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 释放本对象。
    /// </summary>
    /// <remarks>
    /// 本类**不持有任何需要释放的资源**：它只保存一个连接串字符串，
    /// 每次 <see cref="OpenAsync"/> 现开现还（由调用方的 <c>await using</c> 负责关闭），
    /// 故这里无事可做。实现 <see cref="IAsyncDisposable"/> 是为了满足 Task 8 brief 的
    /// Interfaces 清单（把 <see cref="TestDatabase"/> 公布为 <c>IAsyncDisposable</c>），
    /// 使下游可以安全地写 <c>await using var db = new TestDatabase();</c> 而不必
    /// 在将来本类真的持有连接时再改调用点。
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
