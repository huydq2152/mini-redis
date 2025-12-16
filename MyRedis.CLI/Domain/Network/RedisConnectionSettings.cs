namespace MyRedis.CLI.Domain.Network;

/// <summary>
/// Immutable value object representing Redis connection configuration.
/// </summary>
public sealed class RedisConnectionSettings
{
    public string Host { get; }
    public int Port { get; }
    public TimeSpan ConnectionTimeout { get; }
    public TimeSpan ReadTimeout { get; }
    public TimeSpan WriteTimeout { get; }
    
    public RedisConnectionSettings(
        string host = "127.0.0.1",
        int port = 6379,
        TimeSpan? connectionTimeout = null,
        TimeSpan? readTimeout = null,
        TimeSpan? writeTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host cannot be empty", nameof(host));
        if (port <= 0 || port > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535");
            
        Host = host;
        Port = port;
        ConnectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(30);
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        WriteTimeout = writeTimeout ?? TimeSpan.FromSeconds(30);
    }
    
    public override string ToString() => $"{Host}:{Port}";
    
    public static RedisConnectionSettings Default => new();
}