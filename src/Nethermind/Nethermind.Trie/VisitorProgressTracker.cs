// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Trie;

/// <summary>
/// Tracks progress of trie traversal operations using path-based estimation.
/// Uses multi-level prefix tracking to estimate completion percentage even when
/// total node count is unknown and traversal is concurrent/out-of-order.
/// </summary>
public class VisitorProgressTracker
{
    public const int Level3Depth = 4; // 4 nibbles
    private const int MaxNodes = 65536; // 16^4 possible 4-nibble prefixes
    private const int ProgressScale = 10_000; // 0.01% precision
    // Heartbeat check for storage nodes, which never reach the state-node triggers
    private const long HeartbeatCheckMask = (1 << 16) - 1;

    private int _seenCount; // Count of level-3 nodes seen (or estimated from shallow leaves)

    private long _nodeCount;
    private long _lastReportedProgress = -1; // Guarded by _reportLock; negative until the first line
    private long _lastReportTimestamp; // Guarded by _reportLock
    private readonly Lock _reportLock = new();
    private long _totalWorkDone; // Total work done (for display, separate from progress calculation)
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly Action<string>? _logAction;
    private readonly string _operationName;
    private readonly int _reportingInterval;
    private readonly bool _printNodes;
    private readonly TimeSpan? _heartbeatInterval;

    /// <summary>
    /// Creates a tracker that logs the estimated traversal progress at <paramref name="logLevel"/>.
    /// </summary>
    /// <remarks>
    /// With <paramref name="heartbeatInterval"/>, a line is written once the interval has passed, even without progress.
    /// The clock is only checked when a node is visited.
    /// </remarks>
    /// <param name="operationName">Line prefix.</param>
    /// <param name="reportingInterval">State nodes between extra progress re-evaluations.</param>
    /// <param name="printNodes">Whether lines include the visited node count.</param>
    /// <param name="heartbeatInterval">Minimum time between lines, except the final one; <c>null</c> reports every change.</param>
    /// <param name="timeProvider">Defaults to <see cref="TimeProvider.System"/>.</param>
    public VisitorProgressTracker(
        string operationName,
        ILogManager logManager,
        int reportingInterval = 100_000,
        bool printNodes = true,
        LogLevel logLevel = LogLevel.Debug,
        TimeSpan? heartbeatInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logManager);
        if (heartbeatInterval is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero, nameof(heartbeatInterval));
        }

        _operationName = operationName;
        _printNodes = printNodes;
        _heartbeatInterval = heartbeatInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ILogger logger = logManager.GetClassLogger<VisitorProgressTracker>();
        InterfaceLogger underlying = logger.UnderlyingLogger;
        _logAction = logLevel switch
        {
            LogLevel.Info when logger.IsInfo => underlying.Info,
            LogLevel.Debug when logger.IsDebug => underlying.Debug,
            LogLevel.Warn when logger.IsWarn => underlying.Warn,
            LogLevel.Error when logger.IsError => s => underlying.Error(s),
            LogLevel.Trace when logger.IsTrace => underlying.Trace,
            _ => null,
        };
        _reportingInterval = reportingInterval;
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    private string FormatProgress(long progressValue)
    {
        float percentage = Math.Clamp(progressValue / (float)ProgressScale, 0, 1);
        long work = Interlocked.Read(ref _totalWorkDone);
        string workStr = work >= 1_000_000 ? $"{work / 1_000_000.0:F1}M" : $"{work:N0}";
        return _printNodes
            ? $"{_operationName,-25} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} nodes: {workStr,8}"
            : $"{_operationName,-25} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)}";
    }

    /// <summary>
    /// Called when a node is visited during traversal.
    /// Thread-safe: can be called concurrently from multiple threads.
    /// </summary>
    /// <param name="path">The path to the node (used for progress estimation)</param>
    /// <param name="isStorage">True if this is a storage node (tracked in total but not used for progress)</param>
    /// <param name="isLeaf">True if this is a leaf node (used to estimate coverage at level 3)</param>
    public void OnNodeVisited(in TreePath path, bool isStorage = false, bool isLeaf = false)
    {
        // Always count the work done
        long work = Interlocked.Increment(ref _totalWorkDone);
        bool shouldLog = _heartbeatInterval is not null && (work & HeartbeatCheckMask) == 0;

        // Only track state nodes for progress estimation at level 3
        if (!isStorage)
        {
            if (path.Length == Level3Depth)
            {
                // Node at exactly level 3 (4 nibbles): count as 1 node
                Interlocked.Increment(ref _seenCount);
                shouldLog = true;
            }
            else if (isLeaf && path.Length > 0 && path.Length < Level3Depth)
            {
                // Leaf at lower depth: estimate how many level-3 nodes it covers
                // Each level has 16 children, so a leaf at depth d covers 16^(4-d) level-3 nodes
                int coverageDepth = Level3Depth - path.Length;
                int estimatedNodes = 1;
                for (int i = 0; i < coverageDepth; i++)
                {
                    estimatedNodes *= 16;
                }

                // Add estimated coverage
                Interlocked.Add(ref _seenCount, estimatedNodes);
                shouldLog = true;
            }
            // Nodes at depth > Level3Depth are ignored for progress calculation

            // Log progress at intervals (based on state nodes only)
            if (Interlocked.Increment(ref _nodeCount) % _reportingInterval == 0)
            {
                shouldLog = true;
            }
        }

        if (shouldLog)
        {
            LogProgress();
        }
    }

    private void LogProgress()
    {
        if (_logAction is null)
        {
            return;
        }

        // Skip logging for first 5 seconds OR until we've seen at least 1% of nodes
        // This avoids showing noisy early estimates
        double elapsed = _timeProvider.GetElapsedTime(_startTimestamp).TotalSeconds;
        int seen = _seenCount;
        double progress = Math.Min((double)seen / MaxNodes, 1.0);

        if (elapsed < 5.0 && progress < 0.01)
        {
            return;
        }

        long progressValue = (long)(progress * ProgressScale);

        // Decide and write under one lock: no duplicate or out-of-order lines
        lock (_reportLock)
        {
            long now = _timeProvider.GetTimestamp();
            bool isDue = _heartbeatInterval is { } interval
                ? _lastReportedProgress < 0 || _timeProvider.GetElapsedTime(_lastReportTimestamp, now) >= interval
                : progressValue > _lastReportedProgress;
            if (!isDue)
            {
                return;
            }

            _lastReportedProgress = Math.Max(_lastReportedProgress, progressValue);
            _lastReportTimestamp = now;
            _logAction(FormatProgress(_lastReportedProgress));
        }
    }

    /// <summary>
    /// Call when traversal is complete to log final progress.
    /// </summary>
    public void Finish()
    {
        if (_logAction is null)
        {
            return;
        }

        lock (_reportLock)
        {
            if (_lastReportedProgress < ProgressScale)
            {
                _lastReportedProgress = ProgressScale;
                _logAction(FormatProgress(ProgressScale));
            }
        }
    }

    /// <summary>
    /// Gets the current estimated progress (0.0 to 1.0).
    /// </summary>
    public double GetProgress()
    {
        int seen = _seenCount;
        return Math.Min((double)seen / MaxNodes, 1.0);
    }

    /// <summary>
    /// Gets the total number of nodes visited.
    /// </summary>
    public long NodeCount => Interlocked.Read(ref _nodeCount);
}
