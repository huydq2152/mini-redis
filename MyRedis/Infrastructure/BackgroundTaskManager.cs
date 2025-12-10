using MyRedis.Abstractions;

namespace MyRedis.Infrastructure;

/// <summary>
/// Handles background tasks processing
/// Single Responsibility: Background task coordination
/// </summary>
public class BackgroundTaskManager
{
    private readonly IDataStore _dataStore;
    private readonly IExpirationService _expirationService;
    private readonly IConnectionManager _connectionManager;
    private readonly NetworkServer _networkServer;

    public BackgroundTaskManager(
        IDataStore dataStore,
        IExpirationService expirationService,
        IConnectionManager connectionManager,
        NetworkServer networkServer)
    {
        _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
        _expirationService = expirationService ?? throw new ArgumentNullException(nameof(expirationService));
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _networkServer = networkServer ?? throw new ArgumentNullException(nameof(networkServer));
    }

    /// <summary>
    /// Processes background tasks like expiration and idle connection cleanup
    /// </summary>
    public void ProcessBackgroundTasks()
    {
        ProcessExpiredKeys();
        ProcessIdleConnections();
    }

    /// <summary>
    /// Gets the timeout for the next background task
    /// </summary>
    /// <returns>Timeout in milliseconds</returns>
    public int GetNextTimeout()
    {
        int ttlWait = _expirationService.GetNextTimeout();
        int idleWait = _connectionManager.GetNextTimeout();
        int selectTimeout = Math.Min(ttlWait, idleWait);

        return selectTimeout < 0 ? 0 : selectTimeout;
    }

    private void ProcessExpiredKeys()
    {
        var expiredKeys = _expirationService.ProcessExpiredKeys();
        foreach (var key in expiredKeys)
        {
            _dataStore.Remove(key);
            Console.WriteLine($"[TTL] Expired key: {key}");
        }
    }

    private void ProcessIdleConnections()
    {
        var idleConnections = _connectionManager.GetIdleConnections();
        if (idleConnections.Count > 0)
        {
            _networkServer.CloseIdleConnections(idleConnections);
        }
    }
}