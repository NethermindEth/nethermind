// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.EventArg
{
    public class P2PProtocolInitializedEventArgs : ProtocolInitializedEventArgs
    {
        public byte P2PVersion { get; set; }
        public string ClientId { get; set; }
        public IReadOnlyList<Capability> Capabilities { get; set; }
        public int ListenPort { get; set; }

        public P2PProtocolInitializedEventArgs(IProtocolHandler handler) : base(handler)
        {
        }
    }
}
