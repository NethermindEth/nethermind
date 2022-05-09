//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Network;

// Temporary class used for removing snap capability after SnapSync finish.
// Will be removed after implementing missing functionality - serving data via snap protocol.
public class SnapCapabilitySwitcher
{
    private readonly IProtocolsManager _protocolsManager;
    private readonly ProgressTracker _progressTracker;

    public SnapCapabilitySwitcher(IProtocolsManager? protocolsManager, ProgressTracker? progressTracker)
    {
        _protocolsManager = protocolsManager ?? throw new ArgumentNullException(nameof(protocolsManager));
        _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));
    }

    /// <summary>
    /// Add Snap capability if SnapSync is not finished and remove after finished.
    /// </summary>
    public void EnableSnapCapabilityUntilSynced()
    {
        if (!_progressTracker.IsSnapGetRangesFinished())
        {
            _protocolsManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
            _progressTracker.SnapSyncFinished += OnSnapSyncFinished;
        }
    }

    private void OnSnapSyncFinished(object? sender, EventArgs e)
    {
        _progressTracker.SnapSyncFinished -= OnSnapSyncFinished;
        _protocolsManager.RemoveSupportedCapability(new Capability(Protocol.Snap, 1));
    }
}
