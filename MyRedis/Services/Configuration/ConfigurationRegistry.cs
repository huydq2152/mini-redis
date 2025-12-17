using MyRedis.Services.Configuration.Validators;

namespace MyRedis.Services.Configuration;

/// <summary>
/// Centralized registry for all configuration parameters.
/// This is where ALL parameters are defined - single source of truth.
///
/// Organization:
/// - Grouped by category (Memory, Network, Performance, etc.)
/// - Alphabetically sorted within category
/// - Includes metadata for each parameter
///
/// Adding New Parameter:
/// 1. Add entry to appropriate category method
/// 2. Specify validator (use existing or create new)
/// 3. Set hot-reloadable flag
/// 4. That's it! No other code changes needed.
/// </summary>
public static class ConfigurationRegistry
{
    public static void RegisterDefaultParameters(ConfigurationService service)
    {
        RegisterMemoryParameters(service);
        RegisterNetworkParameters(service);
        RegisterPerformanceParameters(service);
        RegisterBackgroundParameters(service);
        RegisterLoggingParameters(service);
    }

    private static void RegisterMemoryParameters(ConfigurationService service)
    {
        // Maximum memory buffer size per connection
        service.Register(new ConfigParameter(
            name: "maxbuffersize",
            description: "Maximum buffer size per connection in bytes",
            defaultValue: "536870912", // 512MB
            type: typeof(long),
            validator: new NumericValidator(min: 1024 * 1024, max: 1024L * 1024 * 1024, allowUnits: true),
            isHotReloadable: false, // Buffers allocated at connection time
            isMutable: true,
            category: "memory",
            introducedInVersion: "1.0"
        ));

        // Lazy free threshold
        service.Register(new ConfigParameter(
            name: "lazyfree-lazy-server-del",
            description: "Threshold for async deletion (elements)",
            defaultValue: "64",
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 10000),
            isHotReloadable: true, // Checked on each DEL operation
            isMutable: true,
            category: "memory",
            introducedInVersion: "1.0"
        ));
    }

    private static void RegisterNetworkParameters(ConfigurationService service)
    {
        // TCP port
        service.Register(new ConfigParameter(
            name: "port",
            description: "TCP listen port",
            defaultValue: "6379",
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 65535),
            isHotReloadable: false, // Server socket bound at startup
            isMutable: false, // Read-only after startup
            category: "network",
            introducedInVersion: "1.0"
        ));

        // Idle timeout
        service.Register(new ConfigParameter(
            name: "timeout",
            description: "Idle connection timeout in seconds (0 = never)",
            defaultValue: "300",
            type: typeof(int),
            validator: new NumericValidator(min: 0, max: 86400), // 0 to 24 hours
            isHotReloadable: true, // Checked on each idle scan
            isMutable: true,
            category: "network",
            introducedInVersion: "1.0"
        ));

        // TCP listen backlog
        service.Register(new ConfigParameter(
            name: "tcp-backlog",
            description: "TCP listen backlog size",
            defaultValue: "128",
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 65535),
            isHotReloadable: false, // Socket option set at startup
            isMutable: true,
            category: "network",
            introducedInVersion: "1.0"
        ));
    }

    private static void RegisterPerformanceParameters(ConfigurationService service)
    {
        // Max commands per loop (fairness)
        service.Register(new ConfigParameter(
            name: "commands-per-loop",
            description: "Max commands processed per connection per iteration",
            defaultValue: "16",
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 1000),
            isHotReloadable: true, // Checked on each iteration
            isMutable: true,
            category: "performance",
            introducedInVersion: "1.0"
        ));

        // Max protocol arguments (DoS protection)
        service.Register(new ConfigParameter(
            name: "max-protocol-args",
            description: "Max arguments per command (DoS protection)",
            defaultValue: "1024",
            type: typeof(int),
            validator: new NumericValidator(min: 10, max: 10000),
            isHotReloadable: true,
            isMutable: true,
            category: "performance",
            introducedInVersion: "1.0"
        ));
    }

    private static void RegisterBackgroundParameters(ConfigurationService service)
    {
        // Background task frequency (Hz)
        service.Register(new ConfigParameter(
            name: "hz",
            description: "Background task frequency (Hz)",
            defaultValue: "10", // 10 Hz = 100ms interval
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 500),
            isHotReloadable: true, // Affects GetNextRunDelay()
            isMutable: true,
            category: "background",
            introducedInVersion: "1.0"
        ));

        // Max keys expired per cycle
        service.Register(new ConfigParameter(
            name: "expire-keys-per-cycle",
            description: "Max keys to expire per background cycle",
            defaultValue: "100",
            type: typeof(int),
            validator: new NumericValidator(min: 1, max: 10000),
            isHotReloadable: true,
            isMutable: true,
            category: "background",
            introducedInVersion: "1.0"
        ));

        // Idle connection check interval
        service.Register(new ConfigParameter(
            name: "idle-check-interval",
            description: "Idle connection check interval in milliseconds",
            defaultValue: "1000", // 1 second
            type: typeof(int),
            validator: new NumericValidator(min: 100, max: 60000), // 100ms to 60s
            isHotReloadable: true,
            isMutable: true,
            category: "background",
            introducedInVersion: "1.0"
        ));
    }

    private static void RegisterLoggingParameters(ConfigurationService service)
    {
        // Log level
        service.Register(new ConfigParameter(
            name: "loglevel",
            description: "Logging level",
            defaultValue: "notice",
            type: typeof(string),
            validator: new EnumValidator("debug", "verbose", "notice", "warning"),
            isHotReloadable: true,
            isMutable: true,
            category: "logging",
            introducedInVersion: "1.0"
        ));
    }
}
