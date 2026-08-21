// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
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
    private readonly IStateSyncPivot _stateSyncPivot;
    private readonly ISpecProvider _specProvider;
    private readonly IBalHealing _balHealing;
    private readonly ILogger _logger;

    private volatile bool _canBalHeal;

    private volatile bool _stateDownloaded;

    public event Action? Changed;

    public SnapP2PCapabilityResolver(
        ISyncConfig syncConfig,
        ISyncModeSelector syncModeSelector,
        ISyncProgressResolver syncProgressResolver,
        IStateSyncPivot stateSyncPivot,
        ISpecProvider specProvider,
        IBalHealing balHealing,
        ILogManager logManager)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _syncProgressResolver = syncProgressResolver;
        _stateSyncPivot = stateSyncPivot;
        _specProvider = specProvider;
        _balHealing = balHealing;
        _logger = logManager.GetClassLogger<SnapP2PCapabilityResolver>();
        _syncModeSelector.Changed += OnSyncModeChanged;
        _stateSyncPivot.FirstPivotSet += OnFirstPivotSet;
        _canBalHeal = ComputeCanBalHeal(_stateSyncPivot.FirstPivotHeader);
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

    private bool StateDownloaded() => _syncProgressResolver.FindBestFullState() >= _syncProgressResolver.SyncPivot.BlockNumber;

    private bool ComputeCanBalHeal(BlockHeader? firstPivotHeader)
    {
        if (!_syncConfig.SnapSync || _balHealing is NoopBalHealing || firstPivotHeader is null) return false;

        return _specProvider.GetSpec(firstPivotHeader).BlockLevelAccessListsEnabled;
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        bool stateWasDownloaded = _stateDownloaded;
        _stateDownloaded = StateDownloaded();
        if (stateWasDownloaded == _stateDownloaded) return;

        if (_logger.IsDebug) _logger.Debug($"State sync {(_stateDownloaded ? "finished" : "in progress")}; snap advertisement updated");
        Changed?.Invoke();
    }

    private void OnFirstPivotSet(object? sender, BlockHeaderEventArgs e)
    {
        bool couldBalHeal = _canBalHeal;
        _canBalHeal = ComputeCanBalHeal(e.Header);
        if (couldBalHeal != _canBalHeal)
        {
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _syncModeSelector.Changed -= OnSyncModeChanged;
        _stateSyncPivot.FirstPivotSet -= OnFirstPivotSet;
    }
}
