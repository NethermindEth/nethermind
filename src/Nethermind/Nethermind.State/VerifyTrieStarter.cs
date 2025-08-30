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
                if (_logger.IsInfo) _logger!.Info($"Collecting trie stats and verifying that no nodes are missing staring from block {stateAtBlock} with state root {stateAtBlock.StateRoot}...");

                if (!worldStateManager.VerifyTrie(stateAtBlock, exitSource.Token))
                {
                    if (_logger.IsError) _logger!.Error($"Verify trie failed");
                }
            }
            catch (OperationCanceledException)
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
}

public interface IVerifyTrieStarter
{
    bool TryStartVerifyTrie(BlockHeader stateAtBlock);
}
