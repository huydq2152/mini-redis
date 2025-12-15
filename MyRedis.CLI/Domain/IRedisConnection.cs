namespace MyRedis.CLI.Domain;

/// <summary>
/// Represents a connection to a Redis server with protocol-level operations.
/// This interface abstracts the low-level networking details and provides
/// a clean contract for Redis protocol communication.
/// </summary>
public interface IRedisConnection : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the connection is currently established.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Sends a command to the Redis server.
    /// </summary>
    /// <param name="command">The command to send</param>
    /// <returns>A task that completes when the command is sent</returns>
    Task SendCommandAsync(RedisCommand command);
    
    /// <summary>
    /// Reads a response from the Redis server.
    /// </summary>
    /// <returns>The response from the server, or null if connection is closed</returns>
    Task<RedisResponse?> ReadResponseAsync();
    
    /// <summary>
    /// Sends multiple commands in a pipeline (batch operation).
    /// </summary>
    /// <param name="commands">The commands to send in batch</param>
    /// <returns>A task that completes when all commands are sent</returns>
    Task SendPipelineAsync(IEnumerable<RedisCommand> commands);
    
    /// <summary>
    /// Reads multiple responses from a pipeline operation.
    /// </summary>
    /// <param name="count">The number of responses to read</param>
    /// <returns>The responses from the pipeline</returns>
    Task<IReadOnlyList<RedisResponse>> ReadPipelineResponsesAsync(int count);
}