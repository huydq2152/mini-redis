using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Service adapter for the existing ResponseWriter
/// </summary>
public class ResponseWriterService : IResponseWriter
{
    public void WriteString(List<byte> buffer, string value)
    {
        ResponseWriter.WriteString(buffer, value);
    }

    public void WriteInt(List<byte> buffer, long value)
    {
        ResponseWriter.WriteInt(buffer, value);
    }

    public void WriteNil(List<byte> buffer)
    {
        ResponseWriter.WriteNil(buffer);
    }

    public void WriteError(List<byte> buffer, int code, string message)
    {
        ResponseWriter.WriteError(buffer, code, message);
    }

    public void WriteArrayHeader(List<byte> buffer, int count)
    {
        ResponseWriter.WriteArrayHeader(buffer, count);
    }
}