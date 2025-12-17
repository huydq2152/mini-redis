namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Event data for configuration change notifications.
/// Used by observer pattern to notify subscribers of parameter changes.
/// </summary>
public class ConfigChangeEvent
{
    /// <summary>
    /// Name of the parameter that changed.
    /// </summary>
    public required string ParameterName { get; init; }

    /// <summary>
    /// Previous value (before change).
    /// </summary>
    public required string OldValue { get; init; }

    /// <summary>
    /// New value (after change).
    /// </summary>
    public required string NewValue { get; init; }

    /// <summary>
    /// When the change occurred (UTC).
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Is this parameter hot-reloadable?
    /// </summary>
    public bool IsHotReloadable { get; init; }
}
