// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Network;

/// <summary>
/// Advertises snap (and snap/2 when enabled) while the node serves snap data or still needs to snap-sync its own state.
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
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    // BAL healing isn't implemented yet - always false until it lands.
    private readonly bool _canBalHeal = false;

    public event Action? Changed;

    public SnapP2PCapabilityResolver(ISyncConfig syncConfig, ISyncModeSelector syncModeSelector, ISpecProvider specProvider, ILogManager logManager)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger<SnapP2PCapabilityResolver>();
        _syncModeSelector.Changed += OnSyncModeChanged;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        bool snapServingEnabled = _syncConfig.SnapServingEnabled == true;
        bool hasCompletedStateSync = (_syncModeSelector.Current & SyncMode.Full) != 0;
        bool requiresSnapForSync = _syncConfig.SnapSync && !hasCompletedStateSync;

        if (!snapServingEnabled && !requiresSnapForSync) return;

        capabilities.Add(SnapCapability);

        // snap/2 drops GetTrieNodes/TrieNodes (EIP-8189). Only advertise it once we no longer need
        // trie nodes ourselves - state sync finished, or snap-syncing with a BAL-heal.
        bool canAdvertiseSnap2 = requiresSnapForSync
            ? _canBalHeal
            : hasCompletedStateSync && _specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;

        if (canAdvertiseSnap2)
        {
            capabilities.Add(Snap2Capability);
        }
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        bool wasSyncing = (e.Previous & SyncMode.Full) == 0;
        bool isSyncing = (e.Current & SyncMode.Full) == 0;
        if (wasSyncing == isSyncing) return;

        if (_logger.IsDebug) _logger.Debug($"State sync {(isSyncing ? "in progress" : "finished")}; snap advertisement {(isSyncing ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;
}
