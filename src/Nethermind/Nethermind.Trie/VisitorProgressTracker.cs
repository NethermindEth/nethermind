// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Threading;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Trie;

/// <summary>
/// Tracks progress of trie traversal operations using path-based estimation.
/// Uses multi-level prefix tracking to estimate completion percentage even when
/// total node count is unknown and traversal is concurrent/out-of-order.
/// </summary>
public class VisitorProgressTracker
{
    private const int Level3Depth = 4; // 4 nibbles
    private const int MaxNodes = 65536; // 16^4 possible 4-nibble prefixes

    private int _seenCount; // Count of level-3 nodes seen (or estimated from shallow leaves)

    private long _nodeCount;
    private long _totalWorkDone; // Total work done (for display, separate from progress calculation)
    private long _maxReportedProgress; // Track max to avoid going backwards
    private readonly DateTime _startTime;
    private readonly ProgressLogger _logger;
    private readonly string _operationName;
    private readonly int _reportingInterval;

    public VisitorProgressTracker(
        string operationName,
        ILogManager logManager,
        int reportingInterval = 100_000)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _operationName = operationName;
        _logger = new ProgressLogger(operationName, logManager);
        _logger.Reset(0, 10000); // Use 10000 for 0.01% precision
        _logger.SetFormat(FormatProgress);
        _reportingInterval = reportingInterval;
        _startTime = DateTime.UtcNow;
    }

    private string FormatProgress(ProgressLogger logger)
    {
        float percentage = Math.Clamp(logger.CurrentValue / 10000f, 0, 1);
        long work = Interlocked.Read(ref _totalWorkDone);
        string workStr = work >= 1_000_000 ? $"{work / 1_000_000.0:F1}M" : $"{work:N0}";
        return $"{_operationName,-25} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} " +
               Progress.GetMeter(percentage, 1) +
               $" nodes: {workStr,8}";
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
        Interlocked.Increment(ref _totalWorkDone);

        // Only track state nodes for progress estimation at level 3
        if (!isStorage)
        {
            int depth = Math.Min(path.Length, Level3Depth);

            if (depth == Level3Depth)
            {
                // Node at level 3 (4 nibbles): count as 1 node
                Interlocked.Increment(ref _seenCount);
            }
            else if (isLeaf && depth > 0)
            {
                // Leaf at lower depth: estimate how many level-3 nodes it covers
                // Each level has 16 children, so a leaf at depth d covers 16^(4-d) level-3 nodes
                int coverageDepth = Level3Depth - depth;
                int estimatedNodes = 1;
                for (int i = 0; i < coverageDepth; i++)
                {
                    estimatedNodes *= 16;
                }

                // Add estimated coverage
                Interlocked.Add(ref _seenCount, estimatedNodes);
            }

            // Log progress at intervals (based on state nodes only)
            if (Interlocked.Increment(ref _nodeCount) % _reportingInterval == 0)
            {
                LogProgress();
            }
        }
    }

    private void LogProgress()
    {
        // Skip logging for first 5 seconds OR until we've seen at least 1% of nodes
        // This prevents early estimates from getting stuck in _maxReportedProgress
        double elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
        int seen = _seenCount;
        double progress = Math.Min((double)seen / MaxNodes, 1.0);

        if (elapsed < 5.0 && progress < 0.01)
        {
            return;
        }

        long progressValue = (long)(progress * 10000);

        // Never report progress lower than previously reported
        long currentMax = _maxReportedProgress;
        while (progressValue > currentMax)
        {
            if (Interlocked.CompareExchange(ref _maxReportedProgress, progressValue, currentMax) == currentMax)
            {
                break;
            }
            currentMax = _maxReportedProgress;
        }

        _logger.Update(Math.Max(progressValue, _maxReportedProgress));
        _logger.LogProgress();
    }

    /// <summary>
    /// Call when traversal is complete to log final progress.
    /// </summary>
    public void Finish()
    {
        _logger.Update(10000);
        _logger.MarkEnd();
        _logger.LogProgress();
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
