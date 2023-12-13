// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public interface IP2PProtocolHandler : IProtocolHandler
    {
        public IReadOnlyList<Capability> AgreedCapabilities { get; }
        public IReadOnlyList<Capability> AvailableCapabilities { get; }
        bool HasAvailableCapability(Capability capability);
        bool HasAgreedCapability(Capability capability);
        void AddSupportedCapability(Capability capability);
    }
}
