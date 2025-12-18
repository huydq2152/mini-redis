namespace MyRedis.Abstractions.Configuration;

/// <summary>
/// Categories for grouping configuration parameters.
/// Used for organizing CONFIG GET output and documentation.
/// </summary>
public enum ConfigCategory
{
    /// <summary>
    /// General configuration parameters (default category).
    /// </summary>
    General,

    /// <summary>
    /// Memory-related parameters (buffer sizes, lazy free, etc.).
    /// </summary>
    Memory,

    /// <summary>
    /// Network-related parameters (port, timeout, backlog, etc.).
    /// </summary>
    Network,

    /// <summary>
    /// Performance-related parameters (command limits, protocol limits, etc.).
    /// </summary>
    Performance,

    /// <summary>
    /// Background task parameters (expiration, idle cleanup, etc.).
    /// </summary>
    Background,

    /// <summary>
    /// Logging-related parameters (log level, etc.).
    /// </summary>
    Logging
}
