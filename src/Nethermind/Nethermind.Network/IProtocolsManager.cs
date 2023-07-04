// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public interface IProtocolsManager
    {
        void AddSupportedCapability(Capability capability);
        void RemoveSupportedCapability(Capability capability);
        void SendNewCapability(Capability capability);
        void AddProtocol(string code, Func<ISession, IProtocolHandler> factory);
        event EventHandler<ProtocolInitializedEventArgs> P2PProtocolInitialized;
    }
}
