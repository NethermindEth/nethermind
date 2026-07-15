// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Network;

/// <summary>
/// Advertises snap/1 while the node serves snap data or still needs to snap-sync its own state.
/// </summary>
/// <remarks>
/// Replaces the former <c>SnapCapabilitySwitcher</c>: instead of adding the capability on start and removing it
/// when state sync reaches <see cref="SyncMode.Full"/>, the contribution is recomputed per session, so a session
/// opened after sync completes simply no longer advertises snap.
/// </remarks>
public class SnapP2PCapabilityResolver : IP2PCapabilityResolver, IDisposable
{
    private static readonly Capability SnapCapability = new(Protocol.Snap, 1);
    private static readonly Capability Snap2Capability = new(Protocol.Snap, 2);

    private readonly ISyncConfig _syncConfig;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ILogger _logger;

    public event Action? Changed;

    public SnapP2PCapabilityResolver(ISyncConfig syncConfig, ISyncModeSelector syncModeSelector, ILogManager logManager)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _logger = logManager.GetClassLogger<SnapP2PCapabilityResolver>();
        _syncModeSelector.Changed += OnSyncModeChanged;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        bool serving = _syncConfig.SnapServingEnabled == true;
        bool syncingState = _syncConfig.SnapSync && (_syncModeSelector.Current & SyncMode.Full) == 0;
        if (serving || syncingState)
        {
            capabilities.Add(SnapCapability);
            if (_syncConfig.Snap2Enabled)
            {
                capabilities.Add(Snap2Capability);
            }
        }
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e)
    {
        // snap/1's contribution only tracks the sync mode while we snap-sync our own state and are not also
        // serving snap; in every other configuration it is constant, so the rebuild is pointless.
        if (_syncConfig.SnapServingEnabled == true || !_syncConfig.SnapSync) return;

        bool wasSyncing = (e.Previous & SyncMode.Full) == 0;
        bool isSyncing = (e.Current & SyncMode.Full) == 0;
        if (wasSyncing == isSyncing) return;

        if (_logger.IsDebug) _logger.Debug($"State sync {(isSyncing ? "in progress" : "finished")}; snap/1 advertisement {(isSyncing ? "enabled" : "disabled")}");
        Changed?.Invoke();
    }

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;
}
