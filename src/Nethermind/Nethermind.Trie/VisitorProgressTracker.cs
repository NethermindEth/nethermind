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
    private const int MaxLevel = 3; // Levels 0-3 (1 to 4 nibbles)

    // Arrays for tracking seen prefixes: 16, 256, 4096, 65536 entries
    private readonly int[][] _seen;
    private readonly int[] _seenCounts = new int[MaxLevel + 1];
    private static readonly int[] MaxAtLevel = { 16, 256, 4096, 65536 };

    private long _nodeCount;
    private long _totalWorkDone; // Total work done (for display, separate from progress calculation)
    private long _maxReportedProgress; // Track max to avoid going backwards
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

        _seen = new int[MaxLevel + 1][];
        for (int level = 0; level <= MaxLevel; level++)
        {
            _seen[level] = new int[MaxAtLevel[level]];
        }
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
    public void OnNodeVisited(in TreePath path, bool isStorage = false)
    {
        // Always count the work done
        Interlocked.Increment(ref _totalWorkDone);

        // Only track state nodes for progress estimation
        if (!isStorage)
        {
            int depth = Math.Min(path.Length, MaxLevel + 1);
            int prefix = 0;

            for (int level = 0; level < depth; level++)
            {
                prefix = (prefix << 4) | path[level];

                // Mark prefix as seen (thread-safe)
                if (Interlocked.CompareExchange(ref _seen[level][prefix], 1, 0) == 0)
                {
                    Interlocked.Increment(ref _seenCounts[level]);
                }
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
        // Use deepest level with >5% coverage for best granularity
        long progressValue = 0;
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > MaxAtLevel[level] / 20)
            {
                double progress = Math.Min((double)seen / MaxAtLevel[level], 1.0);
                progressValue = (long)(progress * 10000);
                break;
            }
        }

        // Fallback to level 0 if no level had >5% coverage
        if (progressValue == 0)
        {
            progressValue = (long)((double)_seenCounts[0] / 16 * 10000);
        }

        // Never report progress lower than previously reported (due to level switching)
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
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > MaxAtLevel[level] / 20)
            {
                return Math.Min((double)seen / MaxAtLevel[level], 1.0);
            }
        }
        return (double)_seenCounts[0] / 16;
    }

    /// <summary>
    /// Gets the total number of nodes visited.
    /// </summary>
    public long NodeCount => Interlocked.Read(ref _nodeCount);
}
