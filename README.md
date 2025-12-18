# MyRedis

A Redis server implementation in C# (.NET 8.0) built from scratch as a learning project. MyRedis implements core Redis functionality with a focus on clean architecture, SOLID principles, and understanding the internals of high-performance in-memory databases.

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Commands Reference](#commands-reference)
- [Performance Characteristics](#performance-characteristics)
- [Project Structure](#project-structure)
- [Learning Objectives](#learning-objectives)
- [Technical Deep Dives](#technical-deep-dives)

## Overview

MyRedis is an educational implementation of Redis that demonstrates:
- **Event-driven architecture** with single-threaded event loops
- **Non-blocking I/O** using Socket.Select()
- **Custom binary protocols** for efficient client-server communication
- **Advanced data structures** (AVL trees, min-heaps, intrusive linked lists)
- **Memory management** strategies (lazy deletion, expiration tracking)
- **Background task systems** with isolated worker threads
- **Hot-reloadable configuration** with persistent storage

This project is ideal for software engineers who want to understand how in-memory databases work under the hood.

## Features

### Implemented Redis Commands (14 total)

#### String Commands
- **GET** `key` - Retrieve string value, returns nil if expired or non-existent
- **SET** `key value` - Store string value, clears any existing TTL

#### Key Management
- **DEL** `key [key ...]` - Delete one or more keys with intelligent lazy deletion
  - Objects with <64 elements: deleted synchronously (~100ns)
  - Objects with ≥64 elements: deleted asynchronously in background
- **KEYS** `pattern` - List all keys (simplified, no glob matching)
- **SCAN** `cursor [MATCH pattern] [COUNT hint]` - Cursor-based iteration with pattern matching
- **EXPIRE** `key seconds` - Set key expiration in seconds

#### Sorted Sets (ZSET)
- **ZADD** `key score member [score member ...]` - Add/update members in sorted set
- **ZRANGE** `key start stop` - Get members by index range (supports negative indices)
- **ZREM** `key member [member ...]` - Remove members from sorted set

#### Time-To-Live
- **TTL** `key` - Get remaining time to expiration (-2 = doesn't exist, -1 = no expiration)

#### Connection & Diagnostics
- **PING** - Health check, returns "PONG"
- **ECHO** `message` - Echo back the message

#### Configuration Management
- **CONFIG GET** `parameter` - Retrieve configuration values (supports glob patterns: `*`, `max*`, etc.)
- **CONFIG SET** `parameter value` - Update configuration at runtime (if hot-reloadable)
- **CONFIG REWRITE** - Save current configuration to redis.conf file

### Data Structures

#### Sorted Set (ZSET)
- **Hybrid architecture** combining Dictionary + AVL Tree
- **Performance**:
  - Add/Remove: O(log n)
  - Score lookup: O(1) via Dictionary
  - Range queries: O(log n + k) where k = result count
- **Features**:
  - Self-balancing AVL tree for ordered iteration
  - Support for negative indices (-1 = last element)
  - Automatic cleanup via destructor for efficient GC

#### AVL Tree
- Self-balancing binary search tree optimized for sorted sets
- **Sorting**: Primary by score (ascending), secondary by member name (lexicographical)
- **Advanced features**:
  - Size tracking in each node for O(log n) rank operations
  - Four rotation cases for rebalancing
  - Range extraction without full traversal
- All operations guaranteed O(log n) worst-case

### Core Systems

#### Event Loop Architecture
- **Single-threaded event loop** using Socket.Select()
- **Resume List pattern** prevents stalled processing on pipelined commands
- **Fairness guarantees**: Round-robin processing (max 16 commands per connection per iteration)
- **Dynamic timeout calculation** based on next expiration/idle check
- **Microsecond latency** with zero lock overhead

#### Network & Protocol
- **Custom binary protocol** with 4-byte length prefixes
- **Non-blocking I/O** with per-connection buffers:
  - Read buffer: 4KB fixed size with efficient compaction
  - Write buffer: Dynamic growth with 512MB safety limit
- **Pipelining support**: Multiple commands in single TCP packet
- **DoS protection**: Configurable limits on arguments and buffer sizes

#### Background Task System

**Two distinct subsystems for different needs:**

1. **Periodic Scheduled Tasks** (BackgroundTaskManager)
   - Runs tasks on regular schedule (e.g., every 100ms)
   - **Expiration Task**: Deletes up to 100 expired keys per cycle
   - **Idle Connection Cleanup**: Closes connections exceeding timeout
   - Integrated with event loop for optimal Select() timeout

2. **Deferred Async Operations** (BackgroundTaskSystem)
   - Executes one-off operations submitted by commands
   - **Category-based isolation**: LazyFree, Persistence (separate worker threads)
   - **LazyFree Category**: Async deletion of large objects (≥64 elements)
   - **Health monitoring**: Per-worker status, queue depth, failure tracking
   - **Graceful shutdown**: Configurable timeouts (5s for LazyFree, 30s for Persistence)

#### Configuration System
- **15 configurable parameters** across 5 categories (Memory, Network, Performance, Background, Logging)
- **Hot-reload support**: Some parameters take effect immediately without restart
- **Observer pattern**: Subscribe to configuration changes
- **Persistent storage**: Load from/save to redis.conf file
- **Type-safe validators**: Numeric ranges, enums, boolean values
- **Configuration categories** using enum (no hardcoded strings)

#### Expiration Management
- **Two-level strategy** for efficiency:
  - **Passive expiration**: Lazy check on access (GET, EXISTS, etc.)
  - **Active expiration**: Background cleanup every 100ms
- **Min-heap priority queue** for efficient sorted expiration tracking
- **Throttled cleanup**: Max 100 keys per cycle to prevent event loop blocking

## Architecture

### Threading Model

```
┌─────────────────────────────────────────────────────────┐
│                   Main Event Loop                       │
│  (Single-threaded - processes all client commands)     │
├─────────────────────────────────────────────────────────┤
│  1. Process Resume List (pipelined commands)            │
│  2. Socket.Select() - wait for network events           │
│  3. Process connections with data                       │
│  4. Run background maintenance tasks                    │
│  5. Repeat                                              │
└─────────────────────────────────────────────────────────┘
                            │
                            ├─────────────────────────────┐
                            ↓                             ↓
                ┌──────────────────────┐   ┌─────────────────────────┐
                │ Background Workers   │   │ Background Workers      │
                │ (LazyFree Category)  │   │ (Persistence Category)  │
                ├──────────────────────┤   ├─────────────────────────┤
                │ • Async object       │   │ • Future AOF writes     │
                │   deletion           │   │ • Future RDB snapshots  │
                │ • Unbounded queue    │   │ • Bounded queue (100)   │
                │ • 5s shutdown        │   │ • 30s shutdown          │
                └──────────────────────┘   └─────────────────────────┘
```

### Design Principles

- **Single Responsibility**: Each class has one clear purpose
- **Dependency Injection**: All services injected via ServiceContainer
- **Interface-based abstractions**: IDataStore, IConfigurationService, ICommandHandler, etc.
- **Factory pattern**: RedisServerFactory for initialization
- **Command pattern**: ICommandHandler for request handling
- **Observer pattern**: Configuration change notifications

## Getting Started

### Prerequisites

- .NET 8.0 SDK or later
- Windows, Linux, or macOS

### Building the Project

```bash
# Clone the repository
git clone <repository-url>
cd MyRedis

# Build the solution
dotnet build MyRedis.sln
```

### Running the Server

```bash
# Start MyRedis server (listens on port 6379 by default)
dotnet run --project MyRedis/MyRedis.csproj
```

Output:
```
[Config] Loading from: redis.conf
[Config] Loaded 11 parameters (0 errors)
[Server] Starting MyRedis server on port 6379...
[Server] Server started, waiting for connections...
```

### Using the Interactive CLI

```bash
# Start the interactive client
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj
```

Example session:
```
MyRedis CLI - Interactive Redis Client
Type 'quit' or 'exit' to disconnect

> SET mykey "Hello, Redis!"
OK

> GET mykey
"Hello, Redis!"

> EXPIRE mykey 30
1

> TTL mykey
30

> ZADD leaderboard 100 "Alice" 200 "Bob" 150 "Charlie"
3

> ZRANGE leaderboard 0 -1
1) "Alice"
2) "Charlie"
3) "Bob"

> CONFIG GET max*
1) "maxbuffersize"
2) "536870912"
3) "max-protocol-args"
4) "1024"
```

### Running the Test Client

```bash
# Start automated tests (requires server to be running)
dotnet run --project MyRedis.Client/MyRedis.Client.csproj
```

## Configuration

### Using the Sample Configuration File

MyRedis includes a comprehensive sample configuration file:

```bash
# Copy the sample file
cp redis.conf.sample redis.conf

# Edit the configuration (all parameters have detailed comments)
# Uncomment and modify the values you want to change

# Start MyRedis - it automatically loads redis.conf
dotnet run --project MyRedis/MyRedis.csproj
```

### Configuration Parameters

#### Memory Parameters
| Parameter | Default | Range | Hot-Reload | Description |
|-----------|---------|-------|------------|-------------|
| `maxbuffersize` | 512MB | 1MB-1GB | No | Max buffer size per connection |
| `lazyfree-lazy-server-del` | 64 | 1-10000 | Yes | Threshold for async deletion |

#### Network Parameters
| Parameter | Default | Range | Hot-Reload | Description |
|-----------|---------|-------|------------|-------------|
| `port` | 6379 | 1-65535 | No | TCP listen port |
| `timeout` | 300 | 0-86400 | Yes | Idle connection timeout (seconds) |
| `tcp-backlog` | 128 | 1-65535 | No | TCP listen backlog size |

#### Performance Parameters
| Parameter | Default | Range | Hot-Reload | Description |
|-----------|---------|-------|------------|-------------|
| `commands-per-loop` | 16 | 1-1000 | Yes | Max commands per iteration |
| `max-protocol-args` | 1024 | 10-10000 | Yes | Max arguments per command |

#### Background Parameters
| Parameter | Default | Range | Hot-Reload | Description |
|-----------|---------|-------|------------|-------------|
| `hz` | 10 | 1-500 | Yes | Background task frequency (Hz) |
| `expire-keys-per-cycle` | 100 | 1-10000 | Yes | Max keys expired per cycle |
| `idle-check-interval` | 1000 | 100-60000 | Yes | Idle check interval (ms) |

#### Logging Parameters
| Parameter | Default | Values | Hot-Reload | Description |
|-----------|---------|--------|------------|-------------|
| `loglevel` | notice | debug, verbose, notice, warning | Yes | Logging verbosity |

### Runtime Configuration

```bash
# View all parameters
CONFIG GET *

# View specific parameter
CONFIG GET timeout

# View parameters matching pattern
CONFIG GET max*

# Change parameter at runtime (if hot-reloadable)
CONFIG SET timeout 600

# Save runtime changes to redis.conf
CONFIG REWRITE
```

## Commands Reference

### String Commands

#### GET
```
GET key
```
Returns the string value of key, or nil if key doesn't exist or has expired.

**Time Complexity**: O(1)

#### SET
```
SET key value
```
Set key to hold the string value. Clears any existing TTL.

**Time Complexity**: O(1)

### Key Management

#### DEL
```
DEL key [key ...]
```
Removes the specified keys. Returns the number of keys removed.

**Time Complexity**: O(N) where N is the number of keys

**Special Behavior**:
- Small objects (<64 elements): Deleted synchronously
- Large objects (≥64 elements): Deleted asynchronously in background

#### KEYS
```
KEYS pattern
```
Returns all keys in the database.

**Time Complexity**: O(N) where N is the number of keys

**Note**: This is a simplified implementation without glob pattern matching

#### SCAN
```
SCAN cursor [MATCH pattern] [COUNT hint]
```
Incrementally iterate the key space.

**Time Complexity**: O(1) for each call, O(N) for complete iteration

#### EXPIRE
```
EXPIRE key seconds
```
Set a timeout on key. After the timeout has expired, the key will be automatically deleted.

**Time Complexity**: O(log N) where N is the number of keys with expiration

**Returns**: 1 if timeout was set, 0 if key doesn't exist

### Sorted Set Commands

#### ZADD
```
ZADD key score member [score member ...]
```
Adds members with scores to the sorted set, or updates scores if members exist.

**Time Complexity**: O(log N * M) where N is the number of elements and M is the number of elements added

**Returns**: Number of elements added (not updated)

#### ZRANGE
```
ZRANGE key start stop
```
Returns the specified range of elements in the sorted set stored at key.

**Time Complexity**: O(log N + M) where N is the number of elements and M is the result count

**Supports negative indices**: -1 means last element, -2 means penultimate element, etc.

#### ZREM
```
ZREM key member [member ...]
```
Removes the specified members from the sorted set.

**Time Complexity**: O(log N * M) where N is the number of elements and M is the number of members removed

**Returns**: Number of members removed

### TTL Commands

#### TTL
```
TTL key
```
Returns the remaining time to live of a key that has a timeout.

**Time Complexity**: O(1)

**Returns**:
- TTL in seconds
- -2 if key doesn't exist
- -1 if key exists but has no associated expiration

### Configuration Commands

#### CONFIG GET
```
CONFIG GET parameter
```
Get configuration parameter value. Supports glob patterns.

**Time Complexity**: O(N) where N is the number of parameters

**Examples**:
- `CONFIG GET *` - Get all parameters
- `CONFIG GET max*` - Get all parameters starting with "max"
- `CONFIG GET timeout` - Get specific parameter

#### CONFIG SET
```
CONFIG SET parameter value
```
Set configuration parameter to new value. Only works for mutable parameters.

**Time Complexity**: O(1)

**Note**: Some parameters are hot-reloadable (take effect immediately), others require restart

#### CONFIG REWRITE
```
CONFIG REWRITE
```
Rewrite the redis.conf file with the current configuration.

**Time Complexity**: O(N) where N is the number of parameters

### Utility Commands

#### PING
```
PING
```
Returns PONG. Used for connection health checks.

**Time Complexity**: O(1)

#### ECHO
```
ECHO message
```
Returns the message.

**Time Complexity**: O(1)

## Performance Characteristics

### Benchmarks (Typical Hardware)

| Operation | Latency | Throughput |
|-----------|---------|------------|
| GET/SET | ~10-50 microseconds | ~20,000-50,000 ops/sec |
| ZADD | ~50-100 microseconds | ~10,000-20,000 ops/sec |
| ZRANGE (small) | ~50-100 microseconds | ~10,000-20,000 ops/sec |
| DEL (small) | ~100 nanoseconds | Very high |
| DEL (large) | Async, no blocking | N/A |

### Scalability Limits

- **Connections**: ~1,000 concurrent connections (Socket.Select() limitation)
- **Memory**: Limited by available system memory
- **Keys**: Millions (hash table performance degrades gradually)
- **Sorted Set Size**: Hundreds of thousands (O(log N) operations remain fast)

### Optimization Techniques Used

1. **String Interning**: Command names reused across requests (zero allocation)
2. **Span<T> Usage**: Zero-copy buffer management with JIT optimizations
3. **Resume List Pattern**: Prevents stalled processing on pipelined commands
4. **Threshold-Based Lazy Deletion**: Async overhead only for large objects
5. **Min-Heap Expiration**: O(log N) insertion, O(1) peek for next expiration
6. **Single-Threaded**: Zero lock overhead, predictable performance

## Project Structure

```
MyRedis/
├── Commands/                   # 14 command handler implementations
│   ├── BaseCommandHandler.cs  # Base class with common utilities
│   ├── GetCommandHandler.cs
│   ├── SetCommandHandler.cs
│   ├── DelCommandHandler.cs
│   ├── ConfigCommandHandler.cs
│   └── ...
├── Core/
│   ├── Background/
│   │   ├── ExpirationTask.cs          # Periodic key expiration
│   │   └── IdleConnectionCleanupTask.cs
│   ├── Network/
│   │   ├── Connection.cs              # Per-connection state
│   │   ├── ProtocolParser.cs          # Binary protocol parser
│   │   └── ResponseWriter.cs          # Response serialization
│   └── Storage/
│       ├── ExpirationManager.cs       # Min-heap for TTL tracking
│       └── IdleManager.cs             # Intrusive linked list for idle detection
├── Infrastructure/
│   ├── Background/
│   │   └── BackgroundTaskManager.cs   # Periodic task coordinator
│   ├── Commands/
│   │   └── CommandProcessor.cs        # Protocol parsing & routing
│   ├── Network/
│   │   └── NetworkServer.cs           # TCP networking with Select()
│   ├── Server/
│   │   ├── RedisServerOrchestrator.cs # Main event loop
│   │   └── RedisServerFactory.cs      # DI container setup
│   └── DependencyInjection/
│       └── ServiceContainer.cs        # Simple DI container
├── Services/
│   ├── Commands/
│   │   ├── CommandRegistry.cs         # Command lookup registry
│   │   └── CommandContext.cs          # Command execution context
│   ├── Configuration/
│   │   ├── ConfigurationService.cs    # In-memory config storage
│   │   ├── ConfigurationRegistry.cs   # Parameter definitions
│   │   ├── ConfigurationFile.cs       # redis.conf I/O
│   │   ├── ConfigParameter.cs         # Parameter metadata
│   │   └── Validators/                # Type validators
│   ├── Network/
│   │   ├── ConnectionManager.cs       # Connection tracking
│   │   └── ResponseWriterService.cs
│   └── Storage/
│       ├── InMemoryDataStore.cs       # Main key-value store
│       └── ExpirationService.cs       # Expiration logic
├── Storage/
│   ├── DataStructures/
│   │   ├── SortedSet.cs              # Hybrid Dict + AVL implementation
│   │   ├── AvlTree.cs                # Self-balancing tree
│   │   └── AvlNode.cs                # Tree node
│   └── Models/
│       ├── RedisEntry.cs             # Unified value + metadata
│       ├── RedisValue.cs
│       └── RedisType.cs              # Type enum
├── System/
│   ├── Tasks/
│   │   ├── BackgroundTaskSystem.cs   # Deferred operation coordinator
│   │   ├── BackgroundTaskDefaults.cs # Category configuration
│   │   └── BackgroundTaskCategory.cs # Category enum
│   └── Workers/
│       ├── CategoryWorker.cs         # Per-category worker thread
│       └── WorkerStatus.cs           # Health monitoring
├── Abstractions/                      # Interface definitions
│   ├── Configuration/
│   │   ├── IConfigurationService.cs
│   │   ├── IConfigParameter.cs
│   │   ├── ConfigCategory.cs         # Category enum
│   │   └── ...
│   └── ...
└── Program.cs                         # Entry point

MyRedis.CLI/                          # Interactive client
MyRedis.Client/                       # Automated test client
```

## Learning Objectives

This project demonstrates understanding of:

### System Design
- Event-driven architecture with non-blocking I/O
- Single-threaded vs multi-threaded concurrency models
- Background task systems with isolated workers
- Resource management and cleanup strategies

### Data Structures & Algorithms
- Self-balancing trees (AVL) with O(log N) guarantees
- Min-heap priority queues for efficient scheduling
- Hash tables with unified entry patterns
- Intrusive data structures for zero-allocation tracking

### Network Programming
- Custom binary protocol design
- Efficient buffer management (zero-copy, compaction)
- Pipelining and command batching
- Connection lifecycle management

### Performance Engineering
- Lock-free single-threaded architecture
- Threshold-based lazy operations
- Memory allocation patterns (interning, pooling)
- JIT optimization techniques (Span<T>, SIMD)

### Software Architecture
- SOLID principles in practice
- Dependency injection without frameworks
- Observer pattern for loose coupling
- Command pattern for extensibility

### Operational Concerns
- Configuration management and hot-reload
- Graceful shutdown with timeout handling
- Health monitoring and diagnostics
- Persistent configuration storage

## Technical Deep Dives

### 1. Why Single-Threaded?

**Problem**: Multi-threaded in-memory databases face:
- Lock contention (microsecond operations dominated by lock overhead)
- Context switching overhead
- Cache coherency issues (MESI protocol penalties)
- Complex synchronization bugs

**Solution**: Single-threaded event loop
- **Zero lock overhead**: All operations sequential
- **Predictable latency**: No context switches, no contention
- **CPU cache friendly**: Hot data stays in L1/L2 cache
- **Simple mental model**: No race conditions, easier to reason about

**Trade-off**: Limited to ~20,000-50,000 ops/sec per core (acceptable for many workloads)

### 2. Resume List Pattern

**Problem**: Stalled Processing Bug
```
1. Client sends 1000 pipelined commands
2. Server reads all into buffer
3. Server processes 16 commands (fairness limit)
4. Server calls Select() with timeout
5. Select() sleeps for 100ms (no network activity)
6. 984 commands sit in buffer, waiting unnecessarily
```

**Solution**: Resume List
```csharp
if (commandsProcessed >= maxCommandsPerLoop && connection.BytesInBuffer > 0)
{
    // Don't sleep in Select(), process this connection again immediately
    _resumeList.Add(connection);
}
```

**Impact**: Prevents artificial latency on pipelined commands while maintaining fairness

### 3. Threshold-Based Lazy Deletion

**Problem**: When to use async deletion?

| Object Size | Sync Cost | Async Overhead | Best Choice |
|-------------|-----------|----------------|-------------|
| 10 elements | 100ns | ~5μs | Sync (overhead > benefit) |
| 1000 elements | ~10μs | ~5μs | Async (prevents blocking) |

**Solution**: Dynamic threshold (default 64 elements)
- Below threshold: Overhead > benefit, use sync
- Above threshold: Benefit > overhead, use async

**Implementation**:
```csharp
private const int LAZYFREE_THRESHOLD = 64;

if (sortedSet.Count >= LAZYFREE_THRESHOLD)
{
    _backgroundTaskSystem.Submit(
        BackgroundTaskCategory.LazyFree,
        () => sortedSet.Clear()
    );
}
else
{
    dataStore.Remove(key); // Sync deletion
}
```

### 4. Two-Level Expiration Strategy

**Why not just passive expiration?**
- Memory leak: Unused keys never deleted

**Why not just active expiration?**
- CPU waste: Scanning millions of keys

**Solution**: Combine both
- **Passive**: Free check on access (amortized cost)
- **Active**: Background cleanup every 100ms (max 100 keys)

**Result**: Memory efficiency + CPU efficiency

### 5. Configuration Threading Model

**Scenario**:
```
Main Thread:      [CONFIG SET timeout 600] → writes config
Background Thread: [Read timeout] → reads config
```

**Question**: Do we need locks?

**Answer**: No, because:
1. Only main thread writes (single-threaded writes = no race)
2. .NET reference assignments are atomic (no torn reads)
3. Background threads seeing stale values for milliseconds is acceptable

**Design**: Lock-free with eventual consistency

## Future Enhancements

Potential areas for expansion:
- Additional data types (Lists, Hashes, Sets, Bitmaps)
- Persistence (AOF, RDB snapshots)
- Replication (master-slave)
- Pub/Sub messaging
- Transactions (MULTI/EXEC)
- Lua scripting
- Cluster mode
- High-concurrency networking (io_uring, IOCP)

## Contributing

This is a learning project. Feel free to fork and experiment!

## License

[Your License Here]

## Acknowledgments

This project is inspired by Redis (created by Salvatore Sanfilippo) and various educational resources on database internals.

---

**Built with ❤️ to understand how Redis works under the hood**
