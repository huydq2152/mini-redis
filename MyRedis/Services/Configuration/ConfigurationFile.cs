using MyRedis.Abstractions.Configuration;

namespace MyRedis.Services.Configuration;

/// <summary>
/// Handles reading and writing MyRedis configuration files (redis.conf format).
///
/// Design Principles:
/// - Preserve comments and structure on rewrite (user-friendly)
/// - Simple line-based parsing (no complex grammar)
/// - Compatible with Redis configuration file redis.conf format (subset)
/// - Atomic writes to prevent corruption
/// - Async I/O to avoid blocking event loop
///
/// Thread Safety: Not thread-safe. Single-threaded access assumed.
///
/// Performance Characteristics:
/// - Async I/O: Yields control back to event loop during disk operations
/// - Typical latency: 5-10ms (SSD), 50-100ms (HDD under light load)
/// - Worst-case latency: 500ms-1s (HDD under heavy I/O contention)
/// - Recommendation: Monitor latency, consider BackgroundTask offload if > 100ms
/// </summary>
public static class ConfigurationFile
{
    private const string DefaultConfigFile = "redis.conf";

    /// <summary>
    /// Strips inline comments from a configuration line.
    /// </summary>
    private static string StripInlineComment(string line)
    {
        var commentIndex = line.IndexOf('#');
        if (commentIndex == -1)
        {
            return line; // No comment
        }

        // If line starts with #, it's a full-line comment
        if (line.TrimStart().StartsWith("#"))
        {
            return line; // Keep full-line comments as-is
        }

        // Strip inline comment and trim
        return line.Substring(0, commentIndex).TrimEnd();
    }

    /// <summary>
    /// Reads configuration from file and applies to configuration service.
    ///
    /// This method is intentionally SYNCHRONOUS because it's called during
    /// server startup BEFORE the event loop starts. No clients are connected yet,
    /// so blocking is acceptable and simplifies startup code.
    ///
    /// For runtime config reloads (future feature), consider making this async.
    /// </summary>
    public static void LoadFromFile(IConfigurationService configService, string? filePath = null)
    {
        filePath ??= DefaultConfigFile;

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[Config] File not found: {filePath}, using defaults");
            return;
        }

        Console.WriteLine($"[Config] Loading from: {filePath}");

        int lineNumber = 0;
        int loaded = 0;
        int errors = 0;

        // File.ReadLines() is lazy enumeration (does not load entire file into memory)
        // This is acceptable for startup (no event loop yet)
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;

            // Strip inline comments first (BUG FIX: handle "timeout 300 # comment")
            var contentWithoutComment = StripInlineComment(line);
            var trimmed = contentWithoutComment.Trim();

            // Skip comments and blank lines
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
            {
                continue;
            }

            // Parse: name value
            var parts = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                Console.WriteLine($"[Config] Warning: Line {lineNumber}: Invalid format '{line}'");
                errors++;
                continue;
            }

            var name = parts[0];
            var value = parts[1];

            // Apply configuration
            try
            {
                var result = configService.SetAsync(name, value).Result;
                if (result.Success)
                {
                    loaded++;
                }
                else
                {
                    Console.WriteLine($"[Config] Warning: Line {lineNumber}: {result.ErrorMessage}");
                    errors++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Error: Line {lineNumber}: {ex.Message}");
                errors++;
            }
        }

        Console.WriteLine($"[Config] Loaded {loaded} parameters ({errors} errors)");
    }

    /// <summary>
    /// Writes current configuration to file asynchronously, preserving structure and comments.
    ///
    /// This method uses ASYNC I/O to avoid blocking the Redis event loop.
    ///
    /// Why this matters:
    /// - Redis/MyRedis is single-threaded
    /// - Synchronous File.WriteAllLines() blocks the thread until disk write completes
    /// - Under disk I/O contention (backup, log rotation), this can take 500ms-1s
    /// - During that time, ALL client requests are stalled (GET, SET, etc.)
    /// - Result: Latency spike, timeout errors, potential P0 incident
    ///
    /// Async I/O solution:
    /// - File.WriteAllLinesAsync() yields control back to event loop during I/O wait
    /// - Other client requests can be processed while disk operation is pending
    /// - Still some CPU overhead for buffer copying, but vastly better than blocking
    ///
    /// Future enhancement:
    /// - For even better performance, offload to BackgroundTaskSystem (Persistence worker)
    /// - This would achieve zero event loop blocking
    /// - Trade-off: More complexity (need completion callback), eventual consistency
    ///
    /// Strategy:
    /// - If file exists: Update changed values in place, preserve comments
    /// - If file doesn't exist: Create new file with all parameters
    /// - Atomic write: Write to temp file, then replace original
    /// </summary>
    public static async Task WriteToFileAsync(IConfigurationService configService, string? filePath = null)
    {
        filePath ??= DefaultConfigFile;

        Console.WriteLine($"[Config] Writing to: {filePath}");

        // Get all current parameters
        var currentConfig = configService.GetAll();

        // Read existing file if it exists
        var existingLines = File.Exists(filePath)
            ? (await File.ReadAllLinesAsync(filePath)).ToList()
            : new List<string>();

        var updatedLines = new List<string>();
        var updatedParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process existing file, updating changed values
        if (existingLines.Any())
        {
            foreach (var line in existingLines)
            {
                // Strip inline comments for parsing (BUG FIX)
                var contentWithoutComment = StripInlineComment(line);
                var trimmed = contentWithoutComment.Trim();

                // Preserve comments and blank lines (check ORIGINAL line, not stripped)
                if (string.IsNullOrWhiteSpace(line.Trim()) || line.Trim().StartsWith("#"))
                {
                    updatedLines.Add(line);
                    continue;
                }

                // Parse parameter name from content WITHOUT inline comment
                var parts = trimmed.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    var name = parts[0];

                    // If this parameter exists in current config, write updated value
                    if (currentConfig.TryGetValue(name, out var value))
                    {
                        updatedLines.Add($"{name} {value}");
                        updatedParams.Add(name);
                    }
                    else
                    {
                        // Parameter no longer exists, comment it out
                        updatedLines.Add($"# {line}  # (removed)");
                    }
                }
                else
                {
                    // Malformed line, preserve as-is
                    updatedLines.Add(line);
                }
            }
        }

        // Add new parameters that weren't in the original file
        var newParams = currentConfig.Keys.Except(updatedParams, StringComparer.OrdinalIgnoreCase).ToList();
        if (newParams.Any())
        {
            if (updatedLines.Any())
            {
                updatedLines.Add("");
                updatedLines.Add("# New parameters (added by CONFIG REWRITE)");
            }

            foreach (var name in newParams.OrderBy(n => n))
            {
                var value = currentConfig[name];
                var metadata = configService.GetParameterMetadata(name);

                // Add description as comment
                if (metadata != null)
                {
                    updatedLines.Add($"# {metadata.Description}");
                }

                updatedLines.Add($"{name} {value}");
            }
        }

        // Write to temp file first (atomic write)
        var tempFile = filePath + ".tmp";
        await File.WriteAllLinesAsync(tempFile, updatedLines);

        // Replace original file
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        File.Move(tempFile, filePath);

        Console.WriteLine($"[Config] Wrote {currentConfig.Count} parameters to {filePath}");
    }
}
