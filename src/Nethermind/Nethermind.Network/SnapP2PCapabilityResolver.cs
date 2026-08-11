// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Network;

/// <summary>
/// Advertises snap while the node serves snap data or still needs to
/// snap-sync its own state.
/// </summary>
/// <remarks>
/// Replaces the former <c>SnapCapabilitySwitcher</c>: instead of adding the capability on start and removing it
/// when state sync reaches <see cref="SyncMode.Full"/>, the contribution is recomputed per session, so a session
/// opened after sync completes simply no longer advertises snap.
/// </remarks>
public class SnapP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private static readonly Capability SnapCapability = new(Protocol.Snap, SnapVersions.Snap1);
    private static readonly Capability Snap2Capability = new(Protocol.Snap, SnapVersions.Snap2);

    private readonly ISyncConfig _syncConfig;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly IStateSyncPivot _stateSyncPivot;
    private readonly ISpecProvider _specProvider;
    private readonly IFlatDbConfig _flatDbConfig;
    private readonly ILogger _logger;
    private bool _canBalHeal;
    public event Action? Changed;

    public SnapP2PCapabilityResolver(ISyncConfig syncConfig, ISyncModeSelector syncModeSelector, IStateSyncPivot stateSyncPivot, ISpecProvider specProvider, IFlatDbConfig flatDbConfig, ILogManager logManager)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _stateSyncPivot = stateSyncPivot;
        _specProvider = specProvider;
        _flatDbConfig = flatDbConfig;
        _canBalHeal = ComputeCanBalHeal(_stateSyncPivot.FirstPivotHeader);
        _logger = logManager.GetClassLogger<SnapP2PCapabilityResolver>();

        _syncModeSelector.Changed += OnSyncModeChanged;
        _stateSyncPivot.FirstPivotSet += OnFirstPivotSet;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        bool serving = _syncConfig.SnapServingEnabled == true;
        bool syncingState = _syncConfig.SnapSync && (_syncModeSelector.Current & SyncMode.Full) == 0;
        if (serving || syncingState)
        {
            capabilities.Add(SnapCapability);
            // snap/2 drops GetTrieNodes/TrieNodes (EIP-8189)
            // we should not advertise snap/2 if we need TrieNodes
            bool canAdvertiseSnap2 = syncingState ? _canBalHeal : _specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;
            if (canAdvertiseSnap2)
            {
                capabilities.Add(Snap2Capability);
            }
        }
    }

    private bool ComputeCanBalHeal(BlockHeader? firstPivotHeader)
    {
        if (!_syncConfig.SnapSync || !_flatDbConfig.Enabled) return false;

        if (firstPivotHeader == null) return false;

        return _specProvider.GetSpec(firstPivotHeader).BlockLevelAccessListsEnabled;
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        // The snap contribution only tracks the sync mode while we snap-sync our own state and are not also
        // serving snap; in every other configuration it is constant, so the rebuild is pointless.
        if (_syncConfig.SnapServingEnabled == true || !_syncConfig.SnapSync) return;

        bool wasSyncing = (e.Previous & SyncMode.Full) == 0;
        bool isSyncing = (e.Current & SyncMode.Full) == 0;
        if (wasSyncing == isSyncing) return;

        if (_logger.IsDebug) _logger.Debug($"State sync {(isSyncing ? "in progress" : "finished")}; snap advertisement {(isSyncing ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    private void OnFirstPivotSet(object? sender, BlockHeaderEventArgs e)
    {
        _canBalHeal = ComputeCanBalHeal(e.Header);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _syncModeSelector.Changed -= OnSyncModeChanged;
        _stateSyncPivot.FirstPivotSet -= OnFirstPivotSet;
    }
}
