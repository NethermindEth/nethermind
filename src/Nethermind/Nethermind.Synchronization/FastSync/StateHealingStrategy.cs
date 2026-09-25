// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// Decides whether state sync heals with block access lists or with trie nodes, and publishes that decision so
/// the advertised snap capabilities match what this node will request. Blindly advertising snap/2 makes state
/// healing unavailable, since snap/2 drops GetTrieNodes (EIP-8189).
/// </summary>
public sealed class StateHealingStrategy(ISyncConfig syncConfig, ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<StateHealingStrategy>();

    private volatile bool _canBalHeal;

    public event Action? Changed;

    public bool CanBalHeal => _canBalHeal;

    public void SetPivot(BlockHeader pivot)
    {
        if (_canBalHeal) return;

        if (!syncConfig.SnapSync || !syncConfig.BalHealing || pivot.BlockAccessListHash is null)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Will Heal state with trie nodes - snap sync: {syncConfig.SnapSync}, " +
                              $"BAL healing: {syncConfig.BalHealing}, pivot: {pivot.Number}, BAL hash: {pivot.BlockAccessListHash}.");
            return;
        }

        _canBalHeal = true;
        if (_logger.IsInfo) _logger.Info($"Will Heal state with block access lists from pivot {pivot.Number}.");
        Changed?.Invoke();
    }
}
