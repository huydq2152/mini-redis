namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Metadata for a configuration parameter.
/// Defines name, type, validation rules, default, hot-reload capability.
///
/// Immutability:
/// - All properties are read-only (set at registration)
/// - Thread-safe by design
/// - No synchronization needed
/// </summary>
public interface IConfigParameter
{
    /// <summary>
    /// Parameter name (e.g., "maxclients", "lazyfree-lazy-server-del").
    /// Lowercase with hyphens (Redis convention).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Human-readable description.
    /// Shown in CONFIG GET output and documentation.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Default value as string.
    /// Used when parameter not explicitly set.
    /// Must be valid according to validator.
    /// </summary>
    string DefaultValue { get; }

    /// <summary>
    /// Current value (may differ from default).
    /// Updated by CONFIG SET.
    /// </summary>
    string CurrentValue { get; }

    /// <summary>
    /// Parameter type (for type-safe access).
    /// </summary>
    Type Type { get; }

    /// <summary>
    /// Validator for this parameter.
    /// Validates all SET operations.
    /// </summary>
    IParameterValidator Validator { get; }

    /// <summary>
    /// Can this parameter be changed at runtime (hot-reload)?
    /// - true: Takes effect immediately (e.g., lazyfree-lazy-server-del)
    /// - false: Requires restart (e.g., port, maxbuffersize)
    /// </summary>
    bool IsHotReloadable { get; }

    /// <summary>
    /// Is this parameter mutable (can be changed via CONFIG SET)?
    /// - true: CONFIG SET allowed (most parameters)
    /// - false: Read-only, computed at runtime (e.g., version, uptime)
    /// </summary>
    bool IsMutable { get; }

    /// <summary>
    /// Category for grouping (e.g., Memory, Network, Performance).
    /// Useful for CONFIG GET by category.
    /// </summary>
    ConfigCategory Category { get; }
}
