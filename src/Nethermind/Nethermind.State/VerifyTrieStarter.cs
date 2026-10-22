// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State;

/// <summary>
/// A small helper class that start VerifyTrie on another thread. Also check if it is already running to prevent running
/// two verify trie at the same time.
/// </summary>
/// <param name="worldStateManager"></param>
/// <param name="exitSource"></param>
/// <param name="logManager"></param>
public class VerifyTrieStarter(IWorldStateManager worldStateManager, IProcessExitSource exitSource, ILogManager logManager) : IVerifyTrieStarter
{
    private readonly ILogger _logger = logManager.GetClassLogger<VerifyTrieStarter>();

    private bool _alreadyRunning = false;

    public bool TryStartVerifyTrie(BlockHeader stateAtBlock)
    {
        if (Interlocked.CompareExchange(ref _alreadyRunning, true, false))
        {
            return false;
        }

        Task.Factory.StartNew(() =>
        {
            try
            {
                if (_logger.IsInfo) _logger.Info($"Collecting trie stats and verifying that no nodes are missing staring from block {stateAtBlock} with state root {stateAtBlock.StateRoot}...");

                if (!worldStateManager.VerifyTrie(stateAtBlock, exitSource.Token))
                {
                    if (_logger.IsError) _logger.Error($"Verify trie failed");
                }
            }
            // Only a shutdown-driven cancellation is expected here; an OCE from any other token means the
            // sweep never completed, so it is treated as a fault rather than a benign cancellation.
            catch (Exception e) when (exitSource.Token.IsCancellationRequested && IsCancellation(e))
            {
                if (_logger.IsInfo) _logger.Info($"Verify trie cancelled");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error in verify trie", e);
            }

        }, TaskCreationOptions.LongRunning);

        return true;
    }

    /// <summary>Whether an exception thrown by the verify-trie run represents cancellation rather than a fault.</summary>
    /// <remarks>
    /// The stats walk runs in parallel (<c>BatchedTrieVisitor</c> / <c>FlatTrieVerifier</c> do
    /// <c>Task.WaitAll</c>), so shutdown cancellation surfaces as an <see cref="AggregateException"/> of
    /// <see cref="OperationCanceledException"/>s, not a single one. Only an aggregate whose leaves are
    /// <em>all</em> cancellations counts as cancelled: a real fault raised alongside the cancellation
    /// leaves a non-cancellation leaf and is still surfaced as an error.
    /// </remarks>
    private static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => IsAllCancellation(aggregate),
        _ => false,
    };

    private static bool IsAllCancellation(AggregateException exception)
    {
        bool any = false;
        foreach (Exception leaf in exception.Flatten().InnerExceptions)
        {
            any = true;
            if (leaf is not OperationCanceledException)
            {
                return false;
            }
        }

        return any;
    }
}

public interface IVerifyTrieStarter
{
    bool TryStartVerifyTrie(BlockHeader stateAtBlock);
}
