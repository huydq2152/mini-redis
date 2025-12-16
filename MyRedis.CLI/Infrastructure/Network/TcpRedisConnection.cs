using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using MyRedis.CLI.Domain.Commands;
using MyRedis.CLI.Domain.Network;

namespace MyRedis.CLI.Infrastructure.Network;

/// <summary>
/// TCP-based implementation of Redis connection using the custom binary protocol.
/// Handles low-level networking and protocol serialization/deserialization.
/// </summary>
public sealed class TcpRedisConnection : IRedisConnection
{
    private readonly TcpClient _client;
    private NetworkStream? _stream;
    private readonly RedisConnectionSettings _settings;
    private bool _disposed;

    public bool IsConnected => _client.Connected && _stream != null && !_disposed;

    public TcpRedisConnection(RedisConnectionSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = new TcpClient();
    }

    /// <summary>
    /// Establishes the connection to the Redis server.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpRedisConnection));

        await _client.ConnectAsync(_settings.Host, _settings.Port);
        _stream = _client.GetStream();
        
        // Configure timeouts
        _client.ReceiveTimeout = (int)_settings.ReadTimeout.TotalMilliseconds;
        _client.SendTimeout = (int)_settings.WriteTimeout.TotalMilliseconds;
    }

    public async Task SendCommandAsync(RedisCommand command)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpRedisConnection));
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");

        var packet = SerializeCommand(command);
        await _stream.WriteAsync(packet);
        await _stream.FlushAsync();
    }

    public async Task<RedisResponse?> ReadResponseAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpRedisConnection));
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");

        // Read response type byte
        var typeBuf = new byte[1];
        var bytesRead = await _stream.ReadAsync(typeBuf, 0, 1);
        if (bytesRead == 0)
            return null; // Connection closed

        return await DeserializeResponseAsync(typeBuf[0]);
    }

    public async Task SendPipelineAsync(IEnumerable<RedisCommand> commands)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpRedisConnection));
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");

        using var ms = new MemoryStream();
        
        foreach (var command in commands)
        {
            var packet = SerializeCommand(command);
            await ms.WriteAsync(packet);
        }

        var batchPacket = ms.ToArray();
        await _stream.WriteAsync(batchPacket);
        await _stream.FlushAsync();
    }

    public async Task<IReadOnlyList<RedisResponse>> ReadPipelineResponsesAsync(int count)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpRedisConnection));
        if (!IsConnected)
            throw new InvalidOperationException("Connection is not established");

        var responses = new List<RedisResponse>();
        
        for (int i = 0; i < count; i++)
        {
            var response = await ReadResponseAsync();
            if (response == null)
                throw new InvalidOperationException("Connection closed unexpectedly during pipeline read");
            responses.Add(response);
        }

        return responses;
    }

    private static byte[] SerializeCommand(RedisCommand command)
    {
        using var ms = new MemoryStream();
        Span<byte> intBuffer = stackalloc byte[4];

        var allParts = command.AllParts;
        
        // Write argument count
        BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)allParts.Count);
        ms.Write(intBuffer);

        // Write each argument with its length prefix
        foreach (var part in allParts)
        {
            var strBytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteUInt32LittleEndian(intBuffer, (uint)strBytes.Length);
            ms.Write(intBuffer);
            ms.Write(strBytes);
        }

        return ms.ToArray();
    }

    private async Task<RedisResponse> DeserializeResponseAsync(byte type)
    {
        return type switch
        {
            0 => NilResponse.Instance,
            1 => await ReadErrorAsync(),
            2 => await ReadStringAsync(),
            3 => await ReadIntegerAsync(),
            4 => await ReadArrayAsync(),
            _ => throw new InvalidOperationException($"Unknown response type: {type}")
        };
    }

    private async Task<ErrorResponse> ReadErrorAsync()
    {
        // Read error code
        var codeBuf = new byte[4];
        await _stream.ReadAsync(codeBuf, 0, 4);
        var errorCode = BitConverter.ToInt32(codeBuf, 0);

        // Read message length
        var lenBuf = new byte[4];
        await _stream.ReadAsync(lenBuf, 0, 4);
        var messageLength = BitConverter.ToInt32(lenBuf, 0);

        // Read error message
        var msgData = new byte[messageLength];
        await _stream.ReadAsync(msgData, 0, messageLength);
        var errorMessage = Encoding.UTF8.GetString(msgData);

        return new ErrorResponse(errorCode, errorMessage);
    }

    private async Task<StringResponse> ReadStringAsync()
    {
        // Read length prefix
        var lenBuf = new byte[4];
        await _stream.ReadAsync(lenBuf, 0, 4);
        var len = BitConverter.ToInt32(lenBuf, 0);

        // Read string data
        var data = new byte[len];
        await _stream.ReadAsync(data, 0, len);

        return new StringResponse(Encoding.UTF8.GetString(data));
    }

    private async Task<IntegerResponse> ReadIntegerAsync()
    {
        var buf = new byte[8];
        await _stream.ReadAsync(buf, 0, 8);
        var value = BitConverter.ToInt64(buf, 0);
        return new IntegerResponse(value);
    }

    private async Task<ArrayResponse> ReadArrayAsync()
    {
        // Read array length
        var countBuf = new byte[4];
        await _stream.ReadAsync(countBuf, 0, 4);
        var count = BitConverter.ToInt32(countBuf, 0);

        var elements = new List<RedisResponse>();
        
        for (int i = 0; i < count; i++)
        {
            // Read element type
            var typeBuf = new byte[1];
            await _stream.ReadAsync(typeBuf, 0, 1);

            // Read element value
            var element = await DeserializeResponseAsync(typeBuf[0]);
            elements.Add(element);
        }

        return new ArrayResponse(elements);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _stream?.Dispose();
        _client?.Dispose();
        _disposed = true;
    }
}