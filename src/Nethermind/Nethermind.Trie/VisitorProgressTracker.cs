// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private readonly ProgressLogger _logger;
    private readonly int _reportingInterval;

    public VisitorProgressTracker(
        string operationName,
        ILogManager logManager,
        int reportingInterval = 100_000)
    {
        ArgumentNullException.ThrowIfNull(logManager);

        _logger = new ProgressLogger(operationName, logManager);
        _logger.Reset(0, 100); // Use 100 as target for percentage display
        _reportingInterval = reportingInterval;

        _seen = new int[MaxLevel + 1][];
        for (int level = 0; level <= MaxLevel; level++)
        {
            _seen[level] = new int[MaxAtLevel[level]];
        }
    }

    /// <summary>
    /// Called when a node is visited during traversal.
    /// Thread-safe: can be called concurrently from multiple threads.
    /// </summary>
    public void OnNodeVisited(in TreePath path)
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

        // Log progress at intervals
        if (Interlocked.Increment(ref _nodeCount) % _reportingInterval == 0)
        {
            LogProgress();
        }
    }

    private void LogProgress()
    {
        // Use deepest level with >5% coverage for best granularity
        for (int level = MaxLevel; level >= 0; level--)
        {
            int seen = _seenCounts[level];
            if (seen > MaxAtLevel[level] / 20)
            {
                double progress = Math.Min((double)seen / MaxAtLevel[level], 1.0);
                _logger.Update((long)(progress * 100));
                _logger.LogProgress();
                return;
            }
        }

        // Fallback to level 0
        _logger.Update((long)((double)_seenCounts[0] / 16 * 100));
        _logger.LogProgress();
    }

    /// <summary>
    /// Call when traversal is complete to log final progress.
    /// </summary>
    public void Finish()
    {
        _logger.Update(100);
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
