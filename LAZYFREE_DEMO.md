# Lazyfree Deletion Optimization Demo

## Overview

This document demonstrates the threshold-based deletion strategy implemented in `DelCommandHandler.cs`, which mirrors Redis's `lazyfree-lazy-server-del` configuration.

## The Problem

**Before optimization:** All SortedSets were deleted asynchronously, regardless of size.

**Issue:**
- **Small SortedSet (1-63 elements):** Sync deletion takes ~100 nanoseconds
- **Async overhead:** Action allocation + Channel enqueue + context switch = ~few microseconds
- **Result:** For small objects, overhead > benefit = performance degradation

## The Solution

**Threshold-based deletion** using `LAZYFREE_THRESHOLD = 64` (matching Redis default):

```csharp
private const int LAZYFREE_THRESHOLD = 64;

private static bool IsLargeObject(object val)
{
    if (val is SortedSet sortedSet)
    {
        // Only async delete if >= 64 elements
        return sortedSet.Count >= LAZYFREE_THRESHOLD;
    }
    return false;
}
```

## Performance Impact

| Object Size | Deletion Strategy | Rationale |
|------------|-------------------|-----------|
| < 64 elements | **Sync** (main thread) | Async overhead > deletion cost |
| ≥ 64 elements | **Async** (background) | Prevents main thread blocking |

## Testing the Optimization

### Test Case 1: Small SortedSet (Sync Deletion)

```bash
# In MyRedis.CLI
127.0.0.1:6379> ZADD small_set 1 a 2 b 3 c
(integer) 3

127.0.0.1:6379> DEL small_set
(integer) 1
```

**Expected behavior:**
- Deleted immediately on main thread (no async overhead)
- No background worker log message
- Fast response time

### Test Case 2: Large SortedSet (Async Deletion)

```bash
# In MyRedis.CLI - Add 100 elements (> threshold)
127.0.0.1:6379> ZADD large_set 1 user1 2 user2 3 user3 ... 100 user100
(integer) 100

127.0.0.1:6379> DEL large_set
(integer) 1
```

**Expected server logs:**
```
[Async] Unlinking large key: large_set (size: 100 elements, threshold: 64)
[BgWorker] Large object destroyed: large_set (size: 100 elements)
```

**Expected behavior:**
- Key immediately removed from data store
- Cleanup happens in background (non-blocking)
- Client gets instant response
- Background worker logs completion after ~500ms

## Code Changes Summary

### 1. Added Count Property to SortedSet
```csharp
// MyRedis/Storage/DataStructures/SortedSet.cs
public int Count => _dict.Count;
```

### 2. Added Threshold Constant
```csharp
// MyRedis/Commands/DelCommandHandler.cs
private const int LAZYFREE_THRESHOLD = 64;
```

### 3. Updated IsLargeObject() Logic
```csharp
private static bool IsLargeObject(object val)
{
    if (val is Storage.DataStructures.SortedSet sortedSet)
    {
        return sortedSet.Count >= LAZYFREE_THRESHOLD; // Threshold check!
    }
    return false;
}
```

### 4. Enhanced Logging
```csharp
Console.WriteLine($"[Async] Unlinking large key: {key} (size: {size} elements, threshold: {LAZYFREE_THRESHOLD})");
Console.WriteLine($"[BgWorker] Large object destroyed: {key} (size: {size} elements)");
```

## References

- **Redis Config:** `lazyfree-lazy-server-del` (default threshold: 64)
- **Redis Source:** `src/lazyfree.c` - `dbAsyncDelete()` function
- **Performance Rationale:** Avoid async overhead when deletion cost < context switch cost

## Future Enhancements

Add thresholds for other data structures:
- **Hash:** 64 fields
- **List:** 64 elements
- **Set:** 64 members
- **String:** Always sync (single allocation)
