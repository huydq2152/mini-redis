using MyRedis.CLI.Domain.Commands;
using MyRedis.CLI.Domain.Network;
using MyRedis.CLI.Infrastructure.Network;

namespace MyRedis.CLI.Application.Services;

/// <summary>
/// High-level Redis client service that orchestrates connection management,
/// command execution, and response handling. This is the main application service
/// that the presentation layer interacts with.
/// </summary>
public sealed class RedisClientService : IDisposable
{
    private readonly RedisConnectionSettings _settings;
    private IRedisConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the client is connected to a Redis server.
    /// </summary>
    public bool IsConnected => _connection?.IsConnected ?? false;

    /// <summary>
    /// Gets the current connection settings.
    /// </summary>
    public RedisConnectionSettings Settings => _settings;

    public RedisClientService(RedisConnectionSettings? settings = null)
    {
        _settings = settings ?? RedisConnectionSettings.Default;
    }

    /// <summary>
    /// Connects to the Redis server using the current settings.
    /// </summary>
    /// <returns>A task representing the connection operation</returns>
    public async Task ConnectAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RedisClientService));

        // Dispose existing connection if any
        _connection?.Dispose();

        // Create and connect new connection
        var tcpConnection = new TcpRedisConnection(_settings);
        await tcpConnection.ConnectAsync();
        _connection = tcpConnection;
    }

    /// <summary>
    /// Executes a single Redis command and returns the response.
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <returns>The response from the Redis server</returns>
    /// <exception cref="InvalidOperationException">Thrown when not connected</exception>
    public async Task<RedisResponse> ExecuteCommandAsync(RedisCommand command)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RedisClientService));
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to Redis server. Call ConnectAsync first.");

        await _connection!.SendCommandAsync(command);
        
        var response = await _connection.ReadResponseAsync();
        if (response == null)
            throw new InvalidOperationException("Connection was closed by the server");
            
        return response;
    }

    /// <summary>
    /// Executes multiple Redis commands in a pipeline for better performance.
    /// </summary>
    /// <param name="commands">The commands to execute</param>
    /// <returns>The responses from the Redis server in the same order</returns>
    public async Task<IReadOnlyList<RedisResponse>> ExecutePipelineAsync(IReadOnlyList<RedisCommand> commands)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RedisClientService));
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to Redis server. Call ConnectAsync first.");
        if (commands.Count == 0)
            return Array.Empty<RedisResponse>();

        await _connection!.SendPipelineAsync(commands);
        return await _connection.ReadPipelineResponsesAsync(commands.Count);
    }

    /// <summary>
    /// Disconnects from the Redis server.
    /// </summary>
    public void Disconnect()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        Disconnect();
        _disposed = true;
    }
}