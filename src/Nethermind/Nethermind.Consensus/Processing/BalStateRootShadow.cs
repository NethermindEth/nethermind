// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Shadow validation lane that recomputes the post-block state root from the block access list and compares
/// it to the header, counting matches, mismatches and errors. It never affects which blocks are accepted.
/// </summary>
/// <remarks>
/// Non-blocking by design: <see cref="Start"/> dispatches the recompute on the thread pool (no cancellation
/// token, so a pre-start cancel cannot fault the awaiter) and <see cref="Compare"/> attaches a continuation
/// that compares values captured up front. A bounded in-flight backlog caps concurrent computations; a run of
/// consecutive errors self-disables the lane. Each lane creates and disposes its OWN read-only trie store: the
/// flat read-only store keeps per-<c>BeginScope</c> snapshot state, so a single shared store would let
/// concurrent lanes race scope setup/teardown (use-after-dispose). It never mutates Lane A's tree.
/// </remarks>
public sealed class BalStateRootShadow
{
    private const int MaxInFlight = 4;
    private const int MaxConsecutiveErrors = 5;

    private readonly Func<IReadOnlyTrieStore> _trieStoreFactory;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    /// <summary>Shared no-op result returned when the lane does not dispatch, used for reference identity in <see cref="Compare"/>.</summary>
    private static readonly Task<Hash256?> s_noWork = Task.FromResult<Hash256?>(null);

    // 1 once the lane has permanently self-disabled. Atomic so the disabling Warn logs exactly once.
    private int _disabled;
    private int _inFlight;
    private int _consecutiveErrors;

    /// <param name="trieStoreFactory">
    /// Factory producing a fresh read-only trie store per computation; typically
    /// <see cref="IWorldStateManager.CreateReadOnlyTrieStore"/>.
    /// </param>
    public BalStateRootShadow(Func<IReadOnlyTrieStore> trieStoreFactory, IBalStateRootConfig config, ILogManager logManager)
    {
        _trieStoreFactory = trieStoreFactory;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<BalStateRootShadow>();
        if (!config.Enabled) _disabled = 1;

        if (_logger.IsInfo) _logger.Info(CapabilityLine(config));
    }

    private static string CapabilityLine(IBalStateRootConfig config) =>
        $"BAL shadow state root: {(config.Enabled ? "enabled" : "disabled")}, hashing: " +
        $"AVX-512F {(Avx512F.IsSupported ? "yes" : "no")}, " +
        $"Vector512 {(Vector512.IsHardwareAccelerated ? "accelerated" : "not accelerated")}, " +
        $"Vector256 {(Vector256.IsHardwareAccelerated ? "accelerated" : "not accelerated")}, " +
        $"physical cores {RuntimeInformation.PhysicalCoreCount}, GPU backend not built";

    /// <summary>Starts a background computation of the shadow state root for a suggested block.</summary>
    /// <param name="parent">The parent block header, providing the pre-state root.</param>
    /// <param name="suggestedBlock">The block being processed, whose BAL drives the computation.</param>
    /// <returns>
    /// A task producing the computed state root, or a completed <c>null</c> task when the lane is disabled,
    /// the block carries no BAL, the in-flight cap is reached, or the computation throws.
    /// </returns>
    public Task<Hash256?> Start(BlockHeader parent, Block suggestedBlock)
    {
        if (Volatile.Read(ref _disabled) != 0 || suggestedBlock.BlockAccessList is not { } bal) return s_noWork;

        if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Metrics.BalShadowRootSkipped);
            return s_noWork;
        }

        long startTimestamp = Stopwatch.GetTimestamp();

        // No CancellationToken: a pre-start cancel would fault the awaited task on fast/empty blocks.
        Task<Hash256?> lane = Task.Run<Hash256?>(() =>
        {
            try
            {
                // Fresh store per lane: the flat read-only store's per-BeginScope snapshot cannot be shared.
                using IReadOnlyTrieStore trieStore = _trieStoreFactory();
                BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);
                Hash256 root = new BalStateRootCalculator(trieStore, _logManager).ComputeRoot(parent, delta);
                Interlocked.Exchange(ref _consecutiveErrors, 0);
                return root;
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"BAL shadow state root computation failed for block {suggestedBlock.ToString(Block.Format.Short)}: {e}");
                Interlocked.Increment(ref Metrics.BalShadowRootErrors);
                if (Interlocked.Increment(ref _consecutiveErrors) >= MaxConsecutiveErrors
                    && Interlocked.Exchange(ref _disabled, 1) == 0
                    && _logger.IsWarn)
                {
                    _logger.Warn($"BAL shadow state root lane self-disabled after {MaxConsecutiveErrors} consecutive errors.");
                }
                return null;
            }
        });

        // Free the in-flight slot and record timing when the computation finishes, regardless of whether
        // Compare is ever called (a rejected block throws before Compare).
        lane.ContinueWith(_ =>
        {
            Metrics.BalShadowRootLastMicros = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds;
            Interlocked.Decrement(ref _inFlight);
        }, TaskScheduler.Default);

        return lane;
    }

    /// <summary>Attaches a non-blocking comparison of the computed shadow root against the processed block's header.</summary>
    /// <param name="lane">The task returned by <see cref="Start"/>.</param>
    /// <param name="processedBlock">The processed block whose header state root is the comparison target.</param>
    /// <remarks>Never blocks: captures the expected root and block identity as values and compares in a continuation.</remarks>
    public void Compare(Task<Hash256?> lane, Block processedBlock)
    {
        if (ReferenceEquals(lane, s_noWork)) return; // no dispatch happened; nothing to compare

        // Capture as values; never hold the Block reference in the continuation.
        Hash256? expected = processedBlock.Header.StateRoot;
        Hash256? blockHash = processedBlock.Hash;
        long blockNumber = (long)processedBlock.Number;

        lane.ContinueWith(t =>
        {
            try
            {
                Hash256? computed = t.IsCompletedSuccessfully ? t.Result : null;
                if (computed is null) return; // computation errored (already counted) or produced nothing

                if (computed == expected)
                {
                    Interlocked.Increment(ref Metrics.BalShadowRootMatches);
                }
                else
                {
                    Interlocked.Increment(ref Metrics.BalShadowRootMismatches);
                    if (_logger.IsWarn) _logger.Warn($"BAL shadow state root mismatch for block {blockNumber} {blockHash}: computed {computed}, header {expected}");
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref Metrics.BalShadowRootErrors);
                if (_logger.IsDebug) _logger.Debug($"BAL shadow state root comparison failed for block {blockNumber} {blockHash}: {e}");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Spin-waits until all in-flight computations complete or the timeout elapses.</summary>
    /// <param name="timeout">Maximum time to wait for the in-flight backlog to drain.</param>
    /// <returns><c>true</c> if the backlog reached zero before the timeout.</returns>
    /// <remarks>Intended for orderly drain points (e.g. before reading run-total metrics); does not block block processing.</remarks>
    public bool WaitForIdle(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        SpinWait spin = default;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Environment.TickCount64 > deadline) return false;
            spin.SpinOnce();
        }
        return true;
    }
}
