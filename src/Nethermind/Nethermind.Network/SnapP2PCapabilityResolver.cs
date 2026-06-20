// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
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

    private readonly ISyncConfig _syncConfig;
    private readonly ISyncModeSelector _syncModeSelector;

    public event Action? Changed;

    public SnapP2PCapabilityResolver(ISyncConfig syncConfig, ISyncModeSelector syncModeSelector)
    {
        _syncConfig = syncConfig;
        _syncModeSelector = syncModeSelector;
        _syncModeSelector.Changed += OnSyncModeChanged;
    }

    public void Resolve(ISet<Capability> capabilities)
    {
        bool serving = _syncConfig.SnapServingEnabled == true;
        bool syncingState = _syncConfig.SnapSync && (_syncModeSelector.Current & SyncMode.Full) == 0;
        if (serving || syncingState)
        {
            capabilities.Add(SnapCapability);
        }
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs e) => Changed?.Invoke();

    public void Dispose() => _syncModeSelector.Changed -= OnSyncModeChanged;
}
