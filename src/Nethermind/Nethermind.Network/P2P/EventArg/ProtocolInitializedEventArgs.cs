// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network.P2P.EventArg
{
    public class ProtocolInitializedEventArgs : System.EventArgs
    {
        public IProtocolHandler Subprotocol { get; }

        public ProtocolInitializedEventArgs(IProtocolHandler handler)
        {
            Subprotocol = handler;
        }
    }
}
