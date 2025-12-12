using System.Runtime.InteropServices;

namespace MyRedis.Storage;

/// <summary>
/// High-performance discriminated union for Redis values with zero-boxing for numeric types.
///
/// PERFORMANCE OPTIMIZATION: Boxing Elimination
///
/// Problem (Before):
/// ```csharp
/// Dictionary<string, object?> _store;
/// _store["counter"] = 42;  // Boxing: Allocates 24 bytes on heap
/// int value = (int)_store["counter"];  // Unboxing
/// ```
///
/// Cost per INCR operation:
/// - 24 bytes heap allocation (object header + int value)
/// - GC pressure: 10K INCR/sec = 240KB/sec garbage
/// - Cache misses: Pointer indirection to heap
///
/// Solution (After):
/// ```csharp
/// [StructLayout(LayoutKind.Explicit)]
/// struct RedisValue {
///     long _int64Value;  // Stored inline, NO boxing
/// }
/// ```
///
/// Benefits:
/// - ✅ Zero allocations for integers (INCR/DECR)
/// - ✅ Zero GC pressure for counter workloads
/// - ✅ Cache-friendly (values stored inline)
/// - ✅ Type safety via discriminator
///
/// Memory Layout (64-bit system):
/// ```
/// Offset 0-7:  RedisType _type (1 byte + 7 padding)
/// Offset 8-15: Union {
///     long _int64Value;      // 8 bytes
///     double _doubleValue;   // 8 bytes
///     object? _objectValue;  // 8 bytes (pointer)
/// }
/// Total: 16 bytes
/// ```
///
/// C-Style Union:
/// Using StructLayout(LayoutKind.Explicit), all value fields overlap at offset 8.
/// Only one field is valid at a time, determined by _type discriminator.
///
/// CRITICAL: Pass by Reference
/// ```csharp
/// // ❌ BAD: Copies 16 bytes on every call
/// void Process(RedisValue value) { }
///
/// // ✅ GOOD: Passes 8-byte pointer, no copy
/// void Process(in RedisValue value) { }
/// ```
///
/// The `in` modifier = ref readonly:
/// - Passes by reference (8 bytes pointer)
/// - Read-only (no mutations)
/// - Perfect for large structs (>16 bytes should always use `in`)
///
/// Comparison to Redis:
/// ```c
/// typedef struct redisObject {
///     unsigned type:4;
///     unsigned encoding:4;
///     unsigned lru:24;
///     int refcount;
///     void *ptr;
/// } robj;
/// ```
///
/// Our RedisValue is simpler (no refcount, no encoding variants) but same concept.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct RedisValue
{
    /// <summary>
    /// Type discriminator - determines which field of the union is valid.
    ///
    /// Offset 0: Ensures type check is first memory access (cache-friendly).
    ///
    /// Usage:
    /// ```csharp
    /// switch (value._type) {
    ///     case RedisType.Integer: return value._int64Value;
    ///     case RedisType.String: return (string)value._objectValue;
    ///     // ...
    /// }
    /// ```
    /// </summary>
    [FieldOffset(0)]
    private readonly RedisType _type;

    /// <summary>
    /// Integer value (INCR/DECR operations) - NO BOXING.
    ///
    /// Offset 8: Overlaps with _doubleValue and _objectValue (union).
    ///
    /// Used for:
    /// - INCR/DECR commands (counters)
    /// - Integer-encoded strings ("42")
    /// - Range: -2^63 to 2^63-1
    ///
    /// Performance:
    /// - Zero allocations (stored inline)
    /// - Zero GC pressure
    /// - Cache-friendly (no pointer indirection)
    /// </summary>
    [FieldOffset(8)]
    private readonly long _int64Value;

    /// <summary>
    /// Double-precision floating point (INCRBYFLOAT, sorted set scores).
    ///
    /// Offset 8: Overlaps with _int64Value and _objectValue (union).
    ///
    /// Used for:
    /// - INCRBYFLOAT command
    /// - Sorted set scores (internally)
    /// - IEEE 754 double precision
    /// </summary>
    [FieldOffset(8)]
    private readonly double _doubleValue;

    /// <summary>
    /// Reference type value (strings, SortedSet, etc.).
    ///
    /// Offset 8: Overlaps with _int64Value and _doubleValue (union).
    ///
    /// Used for:
    /// - String type: string object
    /// - SortedSet type: SortedSet object
    /// - Future: List, Hash, Set objects
    ///
    /// Memory:
    /// - 8 bytes (pointer to heap object)
    /// - Actual object stored on heap
    /// </summary>
    [FieldOffset(8)]
    private readonly object? _objectValue;

    /// <summary>
    /// Gets the Redis type of this value.
    /// </summary>
    public RedisType Type => _type;

    /// <summary>
    /// Checks if this value represents a null/nil value.
    /// </summary>
    public bool IsNull => _type == RedisType.String && _objectValue == null;

    // ==================== Factory Methods ====================

    /// <summary>
    /// Creates a RedisValue for an integer (zero-boxing).
    ///
    /// Usage:
    /// ```csharp
    /// var value = RedisValue.Integer(42);
    /// // NO heap allocation, stored inline
    /// ```
    ///
    /// Performance:
    /// - Zero allocations
    /// - Perfect for INCR/DECR workloads
    /// - 10K INCR/sec = zero GC pressure
    /// </summary>
    public static RedisValue Integer(long value)
    {
        return new RedisValue(RedisType.Integer, int64: value);
    }

    /// <summary>
    /// Creates a RedisValue for a double (zero-boxing).
    /// </summary>
    public static RedisValue Double(double value)
    {
        return new RedisValue(RedisType.Double, doubleVal: value);
    }

    /// <summary>
    /// Creates a RedisValue for a string.
    ///
    /// Note: String is a reference type, stored on heap.
    /// This doesn't eliminate allocations for strings, only for integers.
    /// </summary>
    public static RedisValue String(string? value)
    {
        return new RedisValue(RedisType.String, obj: value);
    }

    /// <summary>
    /// Creates a RedisValue for a sorted set.
    /// </summary>
    public static RedisValue SortedSet(DataStructures.SortedSet sortedSet)
    {
        return new RedisValue(RedisType.SortedSet, obj: sortedSet);
    }

    /// <summary>
    /// Creates a null/nil value.
    /// </summary>
    public static RedisValue Null()
    {
        return new RedisValue(RedisType.String, obj: null);
    }

    // ==================== Private Constructor ====================

    private RedisValue(RedisType type, long int64 = 0, double doubleVal = 0, object? obj = null)
    {
        // Initialize all fields (required for struct)
        _type = type;
        _int64Value = int64;
        _doubleValue = doubleVal;
        _objectValue = obj;
    }

    // ==================== Accessors (with type checking) ====================

    /// <summary>
    /// Gets the integer value (CRITICAL: use `in` when passing this struct).
    ///
    /// Usage:
    /// ```csharp
    /// if (value.TryGetInteger(out long result)) {
    ///     // Use result
    /// }
    /// ```
    ///
    /// Throws if type is not Integer.
    /// </summary>
    public bool TryGetInteger(out long result)
    {
        if (_type == RedisType.Integer)
        {
            result = _int64Value;
            return true;
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// Gets the integer value, throws if wrong type.
    /// </summary>
    public long AsInteger()
    {
        if (_type != RedisType.Integer)
            throw new InvalidOperationException($"Value is {_type}, not Integer");
        return _int64Value;
    }

    /// <summary>
    /// Gets the double value.
    /// </summary>
    public bool TryGetDouble(out double result)
    {
        if (_type == RedisType.Double)
        {
            result = _doubleValue;
            return true;
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// Gets the string value.
    /// </summary>
    public bool TryGetString(out string? result)
    {
        if (_type == RedisType.String)
        {
            result = _objectValue as string;
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>
    /// Gets the string value, throws if wrong type.
    /// </summary>
    public string? AsString()
    {
        if (_type != RedisType.String)
            throw new InvalidOperationException($"Value is {_type}, not String");
        return _objectValue as string;
    }

    /// <summary>
    /// Gets the sorted set value.
    /// </summary>
    public bool TryGetSortedSet(out DataStructures.SortedSet? result)
    {
        if (_type == RedisType.SortedSet)
        {
            result = _objectValue as DataStructures.SortedSet;
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>
    /// Gets any object value (for backward compatibility).
    /// </summary>
    public object? AsObject()
    {
        return _type switch
        {
            RedisType.Integer => _int64Value,      // ⚠️ Boxing here (only when converting to object)
            RedisType.Double => _doubleValue,       // ⚠️ Boxing here
            RedisType.String => _objectValue,
            RedisType.SortedSet => _objectValue,
            _ => null
        };
    }

    // ==================== Utility Methods ====================

    /// <summary>
    /// Returns a string representation for debugging.
    /// </summary>
    public override string ToString()
    {
        return _type switch
        {
            RedisType.Integer => $"Integer({_int64Value})",
            RedisType.Double => $"Double({_doubleValue})",
            RedisType.String => $"String({_objectValue})",
            RedisType.SortedSet => $"SortedSet(Count={((_objectValue as DataStructures.SortedSet)?.Count ?? 0)})",
            _ => "Unknown"
        };
    }
}
