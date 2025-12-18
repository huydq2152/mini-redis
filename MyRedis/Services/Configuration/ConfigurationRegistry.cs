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
        // Rationale for 512MB default:
        // - Connection buffers use dynamic growth (start at 4KB, doubles when full)
        // - 512MB limit prevents DoS attacks from malicious clients sending huge commands
        // - With 10,000 connections, max memory = 5TB (unrealistic worst case)
        // - Typical connection uses 4-16KB (normal command sizes)
        // - This is a safety limit, not a performance bottleneck
        // Origin: MaxBufferSize constant
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

        // Lazy free threshold for async deletion
        // Rationale for 64 elements default (matches Redis lazyfree-lazy-server-del):
        // - Sync deletion of small objects (~1-63 elements): ~100 nanoseconds
        // - Async overhead (Action allocation, Channel enqueue, context switch): ~few microseconds
        // - Below threshold: Overhead > Benefit, so use sync deletion
        // - Above threshold: Benefit > Overhead, so use async deletion to prevent blocking
        // Performance measurements:
        // - SortedSet with 10 elements: ~100ns sync deletion (optimal)
        // - SortedSet with 1000 elements: Async deletion, main thread remains responsive
        // Origin: LazyFreeThreshold constant
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
        // TCP port - Standard Redis port
        // Rationale for 6379 default:
        // - Standard Redis port for compatibility with existing clients
        // - Well-known port registered with IANA
        // - Allows drop-in replacement for Redis in development
        // Origin: TCP listen port in NetworkServer
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

        // Idle connection timeout
        // Rationale for 300 seconds (5 minutes) default:
        // - Conservative value to avoid prematurely closing slow clients
        // - Matches Redis default timeout behavior
        // - Prevents resource exhaustion from abandoned connections
        // - 0 = never timeout (useful for long-running monitoring clients)
        // Origin: IdleTimeoutMs constant
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
        // Rationale for 128 default:
        // - Allows up to 128 simultaneous connection attempts before accept()
        // - OS-level limit, not application limit
        // - 128 is sufficient for typical workloads (bursts of new connections)
        // - Higher values don't necessarily improve performance
        // Origin: Listen backlog parameter in NetworkServer
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
        // Max commands per loop - Fairness and starvation prevention
        // Rationale for 16 commands default:
        // - Redis uses similar limits (processInputBuffer processes in chunks)
        // - Small enough to ensure fairness (low latency for other clients)
        // - Large enough to amortize event loop overhead (efficient pipelining)
        // - Balances throughput vs latency (avoid starvation)
        // - Prevents "greedy" client with pipelined commands from monopolizing server
        // - After 16 commands, yield to other connections (even if more buffered data)
        // - Ensures round-robin fairness: all clients get timeslices
        // Origin: MaxCommandsPerLoop constant
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

        // Max protocol arguments - DoS protection
        // Rationale for 1024 arguments default:
        // - Prevents memory exhaustion attacks from malicious clients
        // - 1024 arguments is sufficient for legitimate commands (e.g., ZADD with many members)
        // - Typical commands have 1-10 arguments
        // - Protects against: command="ZADD key" + 1 million score/member pairs
        // Origin: Max argument count check in ProtocolParser
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
        // Background task frequency - Expiration and maintenance cadence
        // Rationale for 10 Hz (100ms interval) default:
        // - 10 Hz = 100ms interval between background task executions
        // - Matches typical Redis server Hz (default: 10, range: 1-500)
        // - Balance: responsive enough for timely key expiration, light enough CPU usage
        // - Higher Hz = more responsive but higher CPU (more frequent wake-ups)
        // - Lower Hz = less CPU but less responsive (delayed expiration)
        // - 100ms is imperceptible to users but frequent enough for cleanup
        // Origin: intervalMs parameter default in ExpirationTask
        //         intervalMs parameter default in BackgroundTaskBase
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

        // Max keys expired per cycle - Throttling for event loop protection
        // Rationale for 100 keys default:
        // - Limits expiration work to prevent blocking the event loop
        // - Typical execution: 10-50 microseconds (no expired keys)
        // - Heavy load: 100-200 microseconds (100 keys deleted)
        // - If 1000 keys expire simultaneously, they're processed over 10 cycles (1 second)
        // - Similar to Redis ACTIVE_EXPIRE_CYCLE_LOOKUPS_PER_LOOP
        // - Prevents long-running expiration from delaying client commands
        // Origin: maxKeysPerCycle parameter in ExpirationTask
        //         maxWork parameter in ExpirationManager
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
        // Rationale for 1000ms (1 second) default:
        // - Check for idle connections every 1 second
        // - Less frequent than expiration (expiration is more critical)
        // - 1 second delay in detecting idle connections is acceptable
        // - Lower values = more CPU for idle checks (diminishing returns)
        // - Higher values = slower detection of abandoned connections
        // intervalMs parameter in IdleConnectionCleanupTask
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
        // Log level - Verbosity control
        // Rationale for "notice" default:
        // - debug: Very verbose, logs every operation (useful for development/debugging)
        // - verbose: Detailed logs, more than needed for production
        // - notice: Important events only (default for production) ← CHOSEN
        // - warning: Only warnings and errors (minimal logging)
        // "notice" strikes a balance:
        // - Logs important server events (startup, shutdown, errors)
        // - Doesn't flood logs with routine operations
        // - Matches Redis default loglevel
        // - Can be changed at runtime via CONFIG SET for troubleshooting
        // Future: Currently not fully implemented, reserved for future logging system
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
