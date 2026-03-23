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
public class SnapCapabilitySwitcher : IDisposable
{
    private static readonly Capability SnapCapability = new(Protocol.Snap, 1);

    private readonly IProtocolsManager _protocolsManager;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ILogger _logger;
    private volatile bool _isSubscribed;

    public SnapCapabilitySwitcher(IProtocolsManager? protocolsManager, ISyncModeSelector? syncModeSelector, ILogManager? logManager)
    {
        ArgumentNullException.ThrowIfNull(protocolsManager);
        ArgumentNullException.ThrowIfNull(syncModeSelector);
        ArgumentNullException.ThrowIfNull(logManager);

        _protocolsManager = protocolsManager;
        _syncModeSelector = syncModeSelector;
        _logger = logManager.GetClassLogger();
    }

    /// <summary>
    /// Add Snap capability if SnapSync is not finished and remove after finished.
    /// </summary>
    public void EnableSnapCapabilityUntilSynced()
    {
        if (!_isSubscribed)
        {
            _protocolsManager.AddSupportedCapability(SnapCapability);
            _syncModeSelector.Changed += OnSyncModeChanged;
            _isSubscribed = true;
        }

        if (_logger.IsDebug) _logger.Debug("Enabled snap capability");
    }

    private void OnSyncModeChanged(object? sender, SyncModeChangedEventArgs syncMode)
    {
        if ((syncMode.Current & SyncMode.Full) != 0)
        {
            DisableSnapCapability();
            if (_logger.IsInfo) _logger.Info("State sync finished. Disabled snap capability.");
        }
    }

    public void Dispose() => DisableSnapCapability();

    private void DisableSnapCapability()
    {
        if (_isSubscribed)
        {
            _syncModeSelector.Changed -= OnSyncModeChanged;
            _protocolsManager.RemoveSupportedCapability(SnapCapability);
            _isSubscribed = false;
        }
    }
}
