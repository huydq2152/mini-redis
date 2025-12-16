# MyRedis.CLI Walkthrough Guide

A comprehensive guide to using the MyRedis interactive CLI client - a redis-cli clone for testing and interacting with your MyRedis server.

## Table of Contents

1. [Getting Started](#getting-started)
2. [Interactive Mode](#interactive-mode)
3. [File Execution Mode](#file-execution-mode)
4. [Pipeline Mode](#pipeline-mode)
5. [Command Syntax](#command-syntax)
6. [Available Commands](#available-commands)
7. [Advanced Usage](#advanced-usage)
8. [Tips & Tricks](#tips--tricks)

---

## Getting Started

### Prerequisites

- .NET 8.0 SDK installed
- MyRedis server running (default: `127.0.0.1:6379`)

### Building the CLI

```bash
# Build the entire solution
dotnet build MyRedis.sln

# Or build just the CLI project
dotnet build MyRedis.CLI/MyRedis.CLI.csproj
```

### Starting the Server

Before using the CLI, ensure your MyRedis server is running:

```bash
dotnet run --project MyRedis/MyRedis.csproj
```

The server will start listening on `127.0.0.1:6379` by default.

---

## Interactive Mode

Interactive mode provides a REPL (Read-Eval-Print Loop) interface similar to `redis-cli`.

### Basic Usage

```bash
# Connect to default server (127.0.0.1:6379)
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj
```

You'll see:
```
Connected to Redis at 127.0.0.1:6379
Type 'exit' or 'quit' to close the connection.
Type 'help' for available commands.

127.0.0.1:6379>
```

### Example Interactive Session

```
127.0.0.1:6379> PING
"PONG"

127.0.0.1:6379> SET mykey "Hello World"
"OK"

127.0.0.1:6379> GET mykey
"Hello World"

127.0.0.1:6379> SET counter 42
"OK"

127.0.0.1:6379> GET counter
"42"

127.0.0.1:6379> DEL mykey
(integer) 1

127.0.0.1:6379> GET mykey
(nil)

127.0.0.1:6379> quit
Disconnected from Redis.
```

### Connecting to Custom Host/Port

```bash
# Connect to custom host
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h localhost -p 6380

# Using long-form flags
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --host 192.168.1.100 --port 6379
```

### Getting Help

Type `help` or `?` within the interactive session:

```
127.0.0.1:6379> help
Available commands:
  help, ?           - Show this help message
  exit, quit, q     - Exit the CLI
  <redis-command>   - Execute any Redis command

Examples:
  SET key value
  GET key
  PING
  KEYS *
```

### Exiting Interactive Mode

Use any of these commands:
- `exit`
- `quit`
- `q`
- Press `Ctrl+C`

---

## File Execution Mode

Execute multiple Redis commands from a text file.

### Creating a Command File

Create a file `commands.txt`:

```redis
# This is a comment - lines starting with # are ignored
SET user:1:name "Alice Johnson"
SET user:1:age 30
SET user:1:email "alice@example.com"

# Test retrieval
GET user:1:name
GET user:1:age

# Test deletion
DEL user:1:email
GET user:1:email

# Expiration test
SET temp:key "temporary value"
EXPIRE temp:key 300
TTL temp:key
```

### Executing the File

```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- commands.txt
```

Or using explicit flag:
```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -f commands.txt
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --file commands.txt
```

### Output

```
Connected to Redis at 127.0.0.1:6379
Loaded 10 commands from commands.txt
[1/10] SET user:1:name "Alice Johnson"
"OK"
[2/10] SET user:1:age 30
"OK"
[3/10] SET user:1:email "alice@example.com"
"OK"
[4/10] GET user:1:name
"Alice Johnson"
[5/10] GET user:1:age
"30"
[6/10] DEL user:1:email
(integer) 1
[7/10] GET user:1:email
(nil)
[8/10] SET temp:key "temporary value"
"OK"
[9/10] EXPIRE temp:key 300
(integer) 1
[10/10] TTL temp:key
(integer) 300
```

---

## Pipeline Mode

Pipeline mode sends all commands at once and receives all responses in batch, significantly improving performance for large command sets.

### Using Pipeline Mode

```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --pipeline commands.txt
```

### Performance Benefits

Pipeline mode is ideal for:
- **Bulk data loading**: Loading thousands of keys
- **Batch operations**: Multiple SET/GET operations
- **Initialization scripts**: Setting up test data
- **Performance testing**: Measuring server throughput

### Example: Bulk Data Loading

Create `bulk_load.txt`:

```redis
SET product:1:name "Laptop"
SET product:1:price 999.99
SET product:2:name "Mouse"
SET product:2:price 29.99
SET product:3:name "Keyboard"
SET product:3:price 79.99
```

Execute in pipeline mode:

```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --pipeline bulk_load.txt
```

Output:
```
Connected to Redis at 127.0.0.1:6379
Loaded 6 commands from bulk_load.txt
Executing 6 commands in pipeline...
[1] SET product:1:name "Laptop"
"OK"

[2] SET product:1:price 999.99
"OK"

[3] SET product:2:name "Mouse"
"OK"

[4] SET product:2:price 29.99
"OK"

[5] SET product:3:name "Keyboard"
"OK"

[6] SET product:3:price 79.99
"OK"
```

---

## Command Syntax

### Basic Commands

Commands are case-insensitive:
```
PING
ping
PiNg
```

### Arguments Without Spaces

No quotes needed:
```
SET mykey myvalue
GET mykey
```

### Arguments With Spaces

Use single or double quotes:
```
SET greeting "Hello World"
SET message 'This is a test'
```

### Numeric Arguments

No quotes needed:
```
SET counter 42
EXPIRE mykey 300
ZADD myset 1.5 member1
```

### Multi-Argument Commands

```
SET key value
ZADD myset 1.0 member1
ZADD myset 2.5 member2 3.0 member3
ZRANGE myset 0 -1
```

### Comments in Files

Lines starting with `#` are ignored:
```redis
# Initialize user data
SET user:1:name "Alice"
SET user:1:age 30

# This command will be executed
GET user:1:name
```

Blank lines are also ignored:
```redis
SET key1 value1

SET key2 value2


GET key1
```

---

## Available Commands

MyRedis.CLI supports all Redis commands implemented by your MyRedis server. Here are the common ones:

### String Commands

```redis
# Set a key
SET mykey "Hello"

# Get a key
GET mykey

# Delete a key
DEL mykey

# Check if key exists (returns 0 or 1)
EXISTS mykey
```

### Expiration Commands

```redis
# Set a key with value
SET tempkey "temporary"

# Set expiration (seconds)
EXPIRE tempkey 60

# Check time to live
TTL tempkey

# Remove expiration
PERSIST tempkey
```

### Sorted Set Commands

```redis
# Add members with scores
ZADD leaderboard 100 "player1"
ZADD leaderboard 200 "player2"
ZADD leaderboard 150 "player3"

# Get range by index
ZRANGE leaderboard 0 -1

# Get range with scores
ZRANGE leaderboard 0 -1 WITHSCORES
```

### Pattern Matching

```redis
# Get all keys
KEYS *

# Get keys matching pattern
KEYS user:*
KEYS product:*:name
```

### Utility Commands

```redis
# Test connection
PING

# Echo a message
ECHO "Hello Redis"
```

---

## Advanced Usage

### Combining Flags

```bash
# Custom host/port with file execution
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h localhost -p 6380 -f commands.txt

# Custom host/port with pipeline mode
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h 192.168.1.100 -p 6379 --pipeline bulk_load.txt
```

### Getting Command-Line Help

```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --help
```

Output:
```
MyRedis CLI - Interactive Redis client

Usage:
  MyRedis.CLI [options] [file]

Options:
  -h, --host <host>     Redis server host (default: 127.0.0.1)
  -p, --port <port>     Redis server port (default: 6379)
  -f, --file <file>     Execute commands from file
  --pipeline            Use pipeline mode for file execution
  --help, -?            Show this help message

Examples:
  MyRedis.CLI                           # Interactive mode
  MyRedis.CLI -h localhost -p 6380     # Connect to specific host/port
  MyRedis.CLI commands.txt              # Execute file
  MyRedis.CLI --pipeline commands.txt   # Execute file with pipeline
```

### Error Handling

The CLI gracefully handles errors:

```
127.0.0.1:6379> GET
(error) Invalid command syntax

127.0.0.1:6379> WRONGCOMMAND key
(error) Unknown command: WRONGCOMMAND
```

In file mode, invalid lines are skipped:
```
Error loading file: Loaded 5 commands with 2 errors: Line 3: Invalid command ''; Line 7: Invalid command 'BADCMD'
```

---

## Tips & Tricks

### 1. Quick Testing Workflow

Use interactive mode for exploration and development:
```bash
# Terminal 1: Run the server
dotnet run --project MyRedis/MyRedis.csproj

# Terminal 2: Interactive CLI for testing
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj
```

### 2. Debugging with Echo

Use ECHO to verify command processing:
```
127.0.0.1:6379> ECHO "Testing connection"
"Testing connection"
```

### 3. Bulk Data Setup

Create initialization scripts for testing:

`setup_test_data.txt`:
```redis
# Clear old data
DEL user:1:name
DEL user:1:age
DEL user:1:email

# Setup test user
SET user:1:name "Test User"
SET user:1:age 25
SET user:1:email "test@example.com"

# Verify setup
GET user:1:name
GET user:1:age
```

Execute:
```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- setup_test_data.txt
```

### 4. Performance Testing

Use pipeline mode with large datasets:

Generate a file with Python:
```python
# generate_commands.py
with open('bulk_commands.txt', 'w') as f:
    for i in range(10000):
        f.write(f'SET key:{i} "value_{i}"\n')
```

Execute with pipeline:
```bash
python generate_commands.py
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- --pipeline bulk_commands.txt
```

### 5. Response Type Examples

Understand different response formats:

```redis
# String response
127.0.0.1:6379> GET mykey
"Hello World"

# Integer response
127.0.0.1:6379> DEL mykey
(integer) 1

# Nil response (key doesn't exist)
127.0.0.1:6379> GET nonexistent
(nil)

# Array response
127.0.0.1:6379> KEYS user:*
1) "user:1:name"
2) "user:1:age"
3) "user:1:email"

# Nested array (sorted sets with scores)
127.0.0.1:6379> ZRANGE myset 0 -1 WITHSCORES
1) "member1"
2) "1.5"
3) "member2"
4) "2.5"
```

### 6. Quoted String Examples

Test different quoting scenarios:

```redis
SET msg1 "Double quotes: This is a test"
SET msg2 'Single quotes: This is also valid'
SET msg3 NoQuotesNeeded
GET msg1
GET msg2
GET msg3
```

### 7. Keyboard Shortcuts (Interactive Mode)

- `Ctrl+C`: Cancel and exit
- `Enter`: Execute command
- Type and press `Enter`: Submit command

### 8. Working with Multiple Servers

Create shell aliases (Bash/PowerShell):

```bash
# Bash
alias myredis-dev='dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h localhost -p 6379'
alias myredis-staging='dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h staging-server -p 6379'

# PowerShell
function myredis-dev { dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h localhost -p 6379 }
function myredis-staging { dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -h staging-server -p 6379 }
```

---

## Troubleshooting

### Connection Refused

**Error**: `Connection error: Connection refused`

**Solution**: Ensure MyRedis server is running:
```bash
dotnet run --project MyRedis/MyRedis.csproj
```

### Invalid Command Syntax

**Error**: `(error) Invalid command syntax`

**Cause**: Missing arguments or malformed command

**Solution**: Check command syntax:
```redis
# Wrong
SET mykey

# Correct
SET mykey myvalue
```

### File Not Found

**Error**: `Error loading file: File not found: commands.txt`

**Solution**: Use absolute path or relative path from project root:
```bash
dotnet run --project MyRedis.CLI/MyRedis.CLI.csproj -- -f ./path/to/commands.txt
```

---

## Summary

MyRedis.CLI provides three powerful modes for interacting with your MyRedis server:

1. **Interactive Mode**: Best for manual testing, exploration, and debugging
2. **File Execution Mode**: Ideal for running test scripts and repeatable operations
3. **Pipeline Mode**: Optimal for bulk operations and performance testing

Choose the mode that best fits your workflow and enjoy seamless Redis command execution!

For more information about the MyRedis server implementation, see the main project documentation.
