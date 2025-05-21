// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State;

// Fix for CS0452: Use an integer instead of a boolean for the Interlocked.CompareExchange operation.
// The integer will act as a flag (0 = false, 1 = true).

public class VerifyTrieStarter(IWorldStateManager worldStateManager, IProcessExitSource exitSource, ILogManager logManager) : IVerifyTrieStarter
{
    private readonly ILogger _logger = logManager.GetClassLogger<VerifyTrieStarter>();

    private int _alreadyRunning = 0; // Use int instead of bool

    public bool TryStartVerifyTrie(BlockHeader stateAtBlock)
    {
        // CompareExchange now works with the integer flag
        if (Interlocked.CompareExchange(ref _alreadyRunning, 1, 0) != 0)
        {
            return false;
        }

        Task.Factory.StartNew(() =>
        {
            try
            {
                if (_logger.IsInfo) _logger!.Info($"Collecting trie stats and verifying that no nodes are missing staring from block {stateAtBlock} with state root {stateAtBlock.StateRoot}...");

                if (!worldStateManager.VerifyTrie(stateAtBlock, exitSource.Token))
                {
                    if (_logger.IsError) _logger!.Error($"Verify trie failed");
                }
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsError) _logger.Error($"Verify trie cancelled");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error in verify trie", e);
            }
            finally
            {
                // Reset the flag to allow future executions
                Interlocked.Exchange(ref _alreadyRunning, 0);
            }

        }, TaskCreationOptions.LongRunning);

        return true;
    }
}

public interface IVerifyTrieStarter
{
    bool TryStartVerifyTrie(BlockHeader stateAtBlock);
}
