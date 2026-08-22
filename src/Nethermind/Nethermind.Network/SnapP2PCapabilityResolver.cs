// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Network;

/// <summary>
/// Advertises snap while the node serves snap data or still needs to snap-sync its own state; snap/2 is
/// only added once BALs are active on the chain and trie nodes are no longer needed to finish syncing.
/// </summary>
/// <remarks>
/// Replaces the former <c>SnapCapabilitySwitcher</c>: instead of adding the capability on start and removing it
/// once state sync finished, the contribution is recomputed per session, so a session opened after sync completes
/// simply no longer advertises snap.
/// </remarks>
public class SnapP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private static readonly Capability SnapCapability = new(Protocol.Snap, SnapVersions.Snap1);
    private static readonly Capability Snap2Capability = new(Protocol.Snap, SnapVersions.Snap2);

    private readonly ISyncConfig _syncConfig;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockTree _blockTree;
    private readonly IBalHealing _balHealing;
    private readonly ILogger _logger;

    private volatile bool _stateDownloaded;
    private volatile bool _canBalHeal;

    public event Action? Changed;

    public SnapP2PCapabilityResolver(
        ISyncConfig syncConfig,
        ISyncModeSelector syncModeSelector,
        ISyncProgressResolver syncProgressResolver,
        ISpecProvider specProvider,
        IBlockTree blockTree,
        IBalHealing balHealing,
        ILogManager logManager)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _syncProgressResolver = syncProgressResolver;
        _specProvider = specProvider;
        _blockTree = blockTree;
        _balHealing = balHealing;
        _logger = logManager.GetClassLogger<SnapP2PCapabilityResolver>();
        _syncModeSelector.Changed += OnSyncModeChanged;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        bool snapServingEnabled = _syncConfig.SnapServingEnabled == true;
        bool stateDownloaded = _stateDownloaded;
        bool requiresSnapForSync = _syncConfig.SnapSync && !stateDownloaded;

        if (!snapServingEnabled && !requiresSnapForSync) return;

        capabilities.Add(SnapCapability);

        // snap/2 drops GetTrieNodes/TrieNodes (EIP-8189). Only advertise it once we no longer need
        // trie nodes ourselves - state sync finished, or snap-syncing with a BAL-heal.
        bool canAdvertiseSnap2 = requiresSnapForSync
            ? _canBalHeal
            : stateDownloaded && _specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

        if (canAdvertiseSnap2)
        {
            capabilities.Add(Snap2Capability);
        }
    }

    private bool CanBalHeal()
    {
        if (!_syncConfig.SnapSync || !_balHealing.CanHeal) return false;

        // Passing the number lets the header store skip its block-number lookup and read the header directly.
        (ulong number, Hash256 hash) = _syncProgressResolver.SyncPivot;
        BlockHeader? pivot = _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, number);
        return pivot is not null && _specProvider.GetSpec(pivot).BlockLevelAccessListsEnabled;
    }

    private bool StateDownloaded() => _syncProgressResolver.FindBestFullState() >= _syncProgressResolver.SyncPivot.BlockNumber;

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        bool stateWasDownloaded = _stateDownloaded;
        bool couldBalHeal = _canBalHeal;
        _stateDownloaded = StateDownloaded();
        _canBalHeal = CanBalHeal();
        if (stateWasDownloaded == _stateDownloaded && couldBalHeal == _canBalHeal) return;

        if (_logger.IsDebug)
            _logger.Debug($"State sync {(_stateDownloaded ? "finished" : "in progress")}, " +
                          $"BAL healing {(_canBalHeal ? "available" : "unavailable")}; snap advertisement updated");
        Changed?.Invoke();
    }

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;
}
