using MyRedis.Abstractions;
using MyRedis.Core;

namespace MyRedis.Services;

/// <summary>
/// Service adapter for the existing ExpirationManager
/// </summary>
public class ExpirationService : IExpirationService
{
    private readonly ExpirationManager _expirationManager;

    public ExpirationService(ExpirationManager expirationManager)
    {
        _expirationManager = expirationManager ?? throw new ArgumentNullException(nameof(expirationManager));
    }

    public void SetExpiration(string key, int timeoutMs)
    {
        _expirationManager.SetExpiration(key, timeoutMs);
    }

    public void RemoveExpiration(string key)
    {
        _expirationManager.RemoveExpiration(key);
    }

    public bool IsExpired(string key)
    {
        return _expirationManager.IsExpired(key);
    }

    public long? GetTtl(string key)
    {
        return _expirationManager.GetTTL(key);
    }

    public int GetNextTimeout()
    {
        return _expirationManager.GetNextTimeout();
    }

    public IList<string> ProcessExpiredKeys()
    {
        return _expirationManager.ProcessExpiredKeys();
    }
}