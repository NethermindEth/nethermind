// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>
/// Performance metrics specifically designed for AI-agent workloads monitoring.
/// Tracks high-frequency operations like eth_call, getLogs, and debug_trace* to identify performance bottlenecks.
/// </summary>
public static class AIWorkloadMetrics
{
    // Simple counters for basic tracking
    private static long _ethCallCount;
    private static long _getLogsCount;
    private static long _debugTraceCount;
    private static long _requestErrors;
    private static long _requestsThrottled;
    
    // Performance tracking
    private static long _totalEthCallDurationMs;
    private static long _totalGetLogsDurationMs;
    private static long _totalDebugTraceDurationMs;
    
    // Properties for external monitoring access
    public static long EthCallCount => _ethCallCount;
    public static long GetLogsCount => _getLogsCount;
    public static long DebugTraceCount => _debugTraceCount;
    public static long RequestErrors => _requestErrors;
    public static long RequestsThrottled => _requestsThrottled;
    
    public static double AverageEthCallDurationMs => 
        _ethCallCount > 0 ? (double)_totalEthCallDurationMs / _ethCallCount : 0;
    
    public static double AverageGetLogsDurationMs => 
        _getLogsCount > 0 ? (double)_totalGetLogsDurationMs / _getLogsCount : 0;
    
    public static double AverageDebugTraceDurationMs => 
        _debugTraceCount > 0 ? (double)_totalDebugTraceDurationMs / _debugTraceCount : 0;
    
    /// <summary>
    /// Records an eth_call operation with its duration and success status
    /// </summary>
    public static void RecordEthCall(TimeSpan duration, bool success)
    {
        Interlocked.Increment(ref _ethCallCount);
        Interlocked.Add(ref _totalEthCallDurationMs, (long)duration.TotalMilliseconds);
        
        if (!success)
        {
            Interlocked.Increment(ref _requestErrors);
        }
    }
    
    /// <summary>
    /// Records a getLogs operation with its duration, block range, and result count
    /// </summary>
    public static void RecordGetLogs(TimeSpan duration, long blockRange, int resultCount, bool success)
    {
        Interlocked.Increment(ref _getLogsCount);
        Interlocked.Add(ref _totalGetLogsDurationMs, (long)duration.TotalMilliseconds);
        
        if (!success)
        {
            Interlocked.Increment(ref _requestErrors);
        }
    }
    
    /// <summary>
    /// Records a debug_trace operation with its duration and trace type
    /// </summary>
    public static void RecordDebugTrace(TimeSpan duration, string traceType, bool success)
    {
        Interlocked.Increment(ref _debugTraceCount);
        Interlocked.Add(ref _totalDebugTraceDurationMs, (long)duration.TotalMilliseconds);
        
        if (!success)
        {
            Interlocked.Increment(ref _requestErrors);
        }
    }
    
    /// <summary>
    /// Records a request error
    /// </summary>
    public static void RecordError(string errorType)
    {
        Interlocked.Increment(ref _requestErrors);
    }
    
    /// <summary>
    /// Records a throttled request
    /// </summary>
    public static void RecordThrottledRequest(string method)
    {
        Interlocked.Increment(ref _requestsThrottled);
    }
    
    /// <summary>
    /// Resets all metrics (useful for testing)
    /// </summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _ethCallCount, 0);
        Interlocked.Exchange(ref _getLogsCount, 0);
        Interlocked.Exchange(ref _debugTraceCount, 0);
        Interlocked.Exchange(ref _requestErrors, 0);
        Interlocked.Exchange(ref _requestsThrottled, 0);
        Interlocked.Exchange(ref _totalEthCallDurationMs, 0);
        Interlocked.Exchange(ref _totalGetLogsDurationMs, 0);
        Interlocked.Exchange(ref _totalDebugTraceDurationMs, 0);
    }
}