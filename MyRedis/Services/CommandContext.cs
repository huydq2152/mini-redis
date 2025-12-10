using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Implementation of command execution context
/// </summary>
public class CommandContext : ICommandContext
{
    public Connection Connection { get; }
    public IDataStore DataStore { get; }
    public IExpirationService ExpirationService { get; }
    public IResponseWriter ResponseWriter { get; }

    public CommandContext(
        Connection connection,
        IDataStore dataStore,
        IExpirationService expirationService,
        IResponseWriter responseWriter)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        DataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        ExpirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        ResponseWriter = responseWriter ?? throw new ArgumentNullException(nameof(responseWriter));
    }
}