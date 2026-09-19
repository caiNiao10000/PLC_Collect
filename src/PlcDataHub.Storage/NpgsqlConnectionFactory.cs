using Npgsql;

namespace PlcDataHub.Storage;

/// <summary>
/// 数据库连接工厂。刻意不引连接池之外的抽象——
/// 本项目的数据库访问点很少（内省、迁移、写入、查询），过度抽象只会增加理解成本。
/// </summary>
public sealed class NpgsqlConnectionFactory
{
    /// <summary>建立一个连接工厂。</summary>
    /// <exception cref="ArgumentException">连接串为空或只有空白。</exception>
    public NpgsqlConnectionFactory(string connectionString)
    {
        // 空连接串要在构造时响亮拒绝，而不是等到第一次 OpenAsync 再抛 Npgsql 的
        // "Connection string is empty"：后者的失败点在几十层调用之外，
        // 且消息里不含"是哪个配置项没填"的线索，用户无从下手。
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("连接串不能为空", nameof(connectionString));
        }

        ConnectionString = connectionString;
    }

    /// <summary>构造时传入的连接串原文。</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// 开一条新连接。每次调用都新建 <see cref="NpgsqlConnection"/> 并交给调用方释放；
    /// 真正的连接复用由 Npgsql 内部的连接池负责（同一个连接串共享池），
    /// 故这里不需要自己缓存连接对象——缓存反而要处理"连接被服务端断开"的重连逻辑。
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
