namespace MyRedis.Abstractions;

/// <summary>
/// Abstraction for writing responses to clients
/// </summary>
public interface IResponseWriter
{
    /// <summary>
    /// Writes a string response
    /// </summary>
    /// <param name="buffer">The buffer to write to</param>
    /// <param name="value">The string value</param>
    void WriteString(List<byte> buffer, string value);

    /// <summary>
    /// Writes an integer response
    /// </summary>
    /// <param name="buffer">The buffer to write to</param>
    /// <param name="value">The integer value</param>
    void WriteInt(List<byte> buffer, long value);

    /// <summary>
    /// Writes a nil/null response
    /// </summary>
    /// <param name="buffer">The buffer to write to</param>
    void WriteNil(List<byte> buffer);

    /// <summary>
    /// Writes an error response
    /// </summary>
    /// <param name="buffer">The buffer to write to</param>
    /// <param name="code">Error code</param>
    /// <param name="message">Error message</param>
    void WriteError(List<byte> buffer, int code, string message);

    /// <summary>
    /// Writes an array header
    /// </summary>
    /// <param name="buffer">The buffer to write to</param>
    /// <param name="count">Number of elements in the array</param>
    void WriteArrayHeader(List<byte> buffer, int count);
}