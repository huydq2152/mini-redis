using System.Buffers;

namespace MyRedis.Abstractions.Network;

/// <summary>
/// Abstraction for writing responses to clients using the binary protocol.
/// </summary>
public interface IResponseWriter
{
    /// <summary>
    /// Writes a string response to the buffer.
    /// </summary>
    /// <param name="writer">The buffer writer of current connection</param>
    /// <param name="value">The string value to send to the client</param>
    void WriteString(IBufferWriter<byte> writer, string value);

    /// <summary>
    /// Writes an integer response to the buffer.
    /// </summary>
    /// <param name="writer">The buffer writer of current connection</param>
    /// <param name="value">The integer value to send to the client</param>
    void WriteInt(IBufferWriter<byte> writer, long value);

    /// <summary>
    /// Writes a nil (null) response to the buffer.
    /// </summary>
    /// <param name="writer">The buffer writer of current connection</param>
    void WriteNil(IBufferWriter<byte> writer);

    /// <summary>
    /// Writes an error response to the buffer.
    /// </summary>
    /// <param name="writer">The buffer writer of current connection</param>
    /// <param name="code">Error code (currently always 1, but extensible)</param>
    /// <param name="message">Human-readable error message</param>
    void WriteError(IBufferWriter<byte> writer, int code, string message);

    /// <summary>
    /// Writes an array header to the buffer (first part of an array response).
    /// </summary>
    /// <param name="writer">The buffer writer of current connection</param>
    /// <param name="count">Number of elements that will follow</param>
    void WriteArrayHeader(IBufferWriter<byte> writer, int count);
}