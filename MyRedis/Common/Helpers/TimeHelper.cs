namespace MyRedis.Common.Helpers;

public static class TimeHelper
{
    /// <summary>
    /// Gets the current time in milliseconds.
    ///
    /// Uses Environment.TickCount64 instead of DateTime for:
    /// - Better performance (no time zone conversions)
    /// - Monotonic clock (doesn't go backwards with system time changes)
    /// - 64-bit prevents overflow (runs for ~292 million years)
    ///
    /// Use Environment.TickCount64 instead of Stopwatch for:
    /// - Sufficient resolution for TTL calculations (milliseconds)
    /// - Lower CPU cycles than Stopwatch
    ///
    /// Note: This is relative time (milliseconds since system boot),
    /// not absolute wall-clock time. Perfect for TTL calculations.
    /// </summary>
    public static long GetNow() => Environment.TickCount64;
}