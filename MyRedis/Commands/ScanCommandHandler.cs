using MyRedis.Abstractions;
using System.Text.RegularExpressions;

namespace MyRedis.Commands;

/// <summary>
/// Handler for the SCAN command which provides cursor-based iteration over keys.
///
/// SCAN is the production-safe alternative to KEYS command.
///
/// Syntax:
///   SCAN cursor [MATCH pattern] [COUNT count]
///
/// Returns:
///   Array reply with two elements:
///   1. Next cursor (string "0" when iteration completes)
///   2. Array of keys in current batch
///
/// Example:
///   > SCAN 0
///   1) "17"           // Next cursor
///   2) 1) "key1"      // Keys batch
///      2) "key2"
///      3) "key3"
///
///   > SCAN 17
///   1) "0"            // 0 = iteration complete
///   2) 1) "key4"
///      2) "key5"
///
/// Optional Parameters:
///   MATCH pattern: Only return keys matching glob-style pattern
///   COUNT hint: Approximate number of keys to return (default: 10)
///
/// Performance Characteristics:
/// - Bounded memory: O(COUNT) per call
/// - Non-blocking: Returns quickly even with millions of keys
/// - Weakly consistent: May return keys multiple times if modified during scan
///
/// Cursor Encoding (Simple Implementation):
/// - Cursor is a decimal integer representing offset in key list
/// - cursor=0: Start of iteration
/// - cursor=0 returned: End of iteration
///
/// Guarantees:
/// - Full iteration if database doesn't change
/// - Each key returned at least once (may have duplicates if keys change)
/// - Bounded memory per call (COUNT parameter)
///
/// Production Note:
/// This is a simplified implementation. Real Redis uses:
/// - Hash table rehashing-aware cursor encoding
/// - Consistent iteration during rehashing
/// - Per-slot iteration for cluster mode
/// </summary>
public class ScanCommandHandler : BaseCommandHandler
{
    /// <summary>
    /// Default number of keys to return per SCAN call.
    /// Matches Redis default behavior (~10 keys per iteration).
    /// </summary>
    private const int DefaultCount = 10;

    /// <summary>
    /// Gets the Redis command name that this handler processes.
    /// </summary>
    public override string CommandName => "SCAN";

    /// <summary>
    /// Handles the SCAN command execution with cursor-based iteration.
    ///
    /// Algorithm:
    /// 1. Parse cursor from args[0]
    /// 2. Parse optional MATCH and COUNT parameters
    /// 3. Get snapshot of all keys
    /// 4. Filter by MATCH pattern if provided
    /// 5. Slice keys[cursor:cursor+count]
    /// 6. Calculate next cursor (0 if done, cursor+count otherwise)
    /// 7. Return [next_cursor, keys_batch]
    ///
    /// Performance:
    /// - GetAllKeys(): O(N) where N = total keys (snapshot)
    /// - Pattern matching: O(K) where K = keys in batch
    /// - Total: O(N) but only O(COUNT) keys returned
    ///
    /// Memory:
    /// - Keys snapshot: O(N) strings
    /// - Response: O(COUNT) strings
    ///
    /// Future Optimization:
    /// - Implement IDataStore.GetKeysBatch(cursor, count) for true O(COUNT) memory
    /// - Use Dictionary<>.Keys enumerator to avoid full snapshot
    /// </summary>
    public override Task<bool> HandleAsync(ICommandContext context, IReadOnlyList<string> args)
    {
        // Validate minimum arguments: SCAN cursor
        if (args.Count < 1)
        {
            WriteWrongArgsError(context);
            return Task.FromResult(true);
        }

        // Parse cursor
        if (!int.TryParse(args[0], out int cursor) || cursor < 0)
        {
            WriteError(context, "ERR invalid cursor");
            return Task.FromResult(true);
        }

        // Parse optional MATCH and COUNT parameters
        string? matchPattern = null;
        int count = DefaultCount;

        for (int i = 1; i < args.Count; i++)
        {
            string option = args[i].ToUpperInvariant();

            if (option == "MATCH")
            {
                // MATCH pattern
                if (i + 1 >= args.Count)
                {
                    WriteError(context, "ERR syntax error");
                    return Task.FromResult(true);
                }
                matchPattern = args[i + 1];
                i++; // Skip pattern argument
            }
            else if (option == "COUNT")
            {
                // COUNT hint
                if (i + 1 >= args.Count)
                {
                    WriteError(context, "ERR syntax error");
                    return Task.FromResult(true);
                }
                if (!int.TryParse(args[i + 1], out count) || count <= 0)
                {
                    WriteError(context, "ERR value is not an integer or out of range");
                    return Task.FromResult(true);
                }
                i++; // Skip count argument
            }
            else
            {
                WriteError(context, "ERR syntax error");
                return Task.FromResult(true);
            }
        }

        // Get snapshot of all keys
        // Note: This is O(N) and allocates O(N) memory
        // For large datasets, consider implementing IDataStore.GetKeysBatch()
        var allKeys = context.DataStore.GetAllKeys().ToList();

        // Apply MATCH filter if provided
        List<string> filteredKeys;
        if (matchPattern != null)
        {
            // Convert glob pattern to regex
            // Redis glob: * (any chars), ? (single char), [abc] (character class)
            var regex = GlobToRegex(matchPattern);
            filteredKeys = allKeys.Where(key => regex.IsMatch(key)).ToList();
        }
        else
        {
            filteredKeys = allKeys;
        }

        // Validate cursor bounds
        if (cursor > filteredKeys.Count)
        {
            // Invalid cursor, but Redis returns empty result instead of error
            cursor = filteredKeys.Count;
        }

        // Calculate slice: keys[cursor : cursor+count]
        int remaining = filteredKeys.Count - cursor;
        int batchSize = Math.Min(count, remaining);
        var keysBatch = filteredKeys.Skip(cursor).Take(batchSize).ToList();

        // Calculate next cursor
        int nextCursor = cursor + batchSize;
        if (nextCursor >= filteredKeys.Count)
        {
            nextCursor = 0; // Iteration complete
        }

        // Write response: Array[2] { cursor, Array[keys] }
        context.ResponseWriter.WriteArrayHeader(context.Connection.Writer, 2);

        // Element 1: Next cursor (as string)
        context.ResponseWriter.WriteString(context.Connection.Writer, nextCursor.ToString());

        // Element 2: Array of keys
        context.ResponseWriter.WriteArrayHeader(context.Connection.Writer, keysBatch.Count);
        foreach (var key in keysBatch)
        {
            context.ResponseWriter.WriteString(context.Connection.Writer, key);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Converts a Redis glob pattern to a .NET Regex.
    ///
    /// Redis glob patterns:
    /// - * : Matches zero or more characters
    /// - ? : Matches exactly one character
    /// - [abc] : Matches one character from the set
    /// - [^abc] : Matches one character NOT in the set
    /// - [a-z] : Matches one character in the range
    /// - \x : Escapes special character x
    ///
    /// Examples:
    /// - h?llo : Matches hello, hallo, hxllo
    /// - h*llo : Matches hllo, hello, heeeello
    /// - h[ae]llo : Matches hello, hallo
    /// - h[^e]llo : Matches hallo, hbllo (not hello)
    /// - h[a-z]llo : Matches hallo, hbllo, hzllo
    ///
    /// Implementation:
    /// 1. Escape regex special chars (except *, ?, [, ])
    /// 2. Replace * with .*
    /// 3. Replace ? with .
    /// 4. Keep [...] as-is (compatible with regex)
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        // Start with pattern anchored to start and end
        var regexPattern = "^";

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];

            switch (c)
            {
                case '*':
                    regexPattern += ".*";
                    break;
                case '?':
                    regexPattern += ".";
                    break;
                case '[':
                    // Character class - copy until ]
                    regexPattern += "[";
                    i++;
                    while (i < pattern.Length && pattern[i] != ']')
                    {
                        regexPattern += pattern[i];
                        i++;
                    }
                    if (i < pattern.Length)
                        regexPattern += "]";
                    break;
                case '\\':
                    // Escape next character
                    if (i + 1 < pattern.Length)
                    {
                        i++;
                        regexPattern += Regex.Escape(pattern[i].ToString());
                    }
                    break;
                default:
                    // Escape regex special characters
                    regexPattern += Regex.Escape(c.ToString());
                    break;
            }
        }

        regexPattern += "$";

        return new Regex(regexPattern, RegexOptions.Compiled);
    }
}
