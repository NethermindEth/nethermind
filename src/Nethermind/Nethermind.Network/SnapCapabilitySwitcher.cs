// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//

using System;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Network;

// Temporary class used for removing snap capability after SnapSync finish.
// Will be removed after implementing missing functionality - serving data via snap protocol.
public class SnapCapabilitySwitcher
{
    private readonly IProtocolsManager _protocolsManager;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ILogger _logger;

    public SnapCapabilitySwitcher(IProtocolsManager? protocolsManager, ISyncModeSelector? syncModeSelector, ILogManager? logManager)
    {
        _protocolsManager = protocolsManager ?? throw new ArgumentNullException(nameof(protocolsManager));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Add Snap capability if SnapSync is not finished and remove after finished.
    /// </summary>
    public void EnableSnapCapabilityUntilSynced()
    {
        _protocolsManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
        _syncModeSelector.Changed += OnSyncModeChanged;
        if (_logger.IsDebug) _logger.Debug("Enabled snap capability");
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs syncMode)
    {
        if ((syncMode.Current & SyncMode.Full) != 0)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
            _protocolsManager.RemoveSupportedCapability(new Capability(Protocol.Snap, 1));
            if (_logger.IsInfo) _logger.Info("State sync finished. Disabled snap capability.");
        }
    }
}
