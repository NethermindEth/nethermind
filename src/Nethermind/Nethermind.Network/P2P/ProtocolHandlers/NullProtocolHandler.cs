// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.ProtocolHandlers
{
    public class NullProtocolHandler : IProtocolHandler
    {
        private NullProtocolHandler()
        {
        }

        public static IProtocolHandler Instance { get; } = new NullProtocolHandler();

        public void Dispose()
        {
        }

        public string Name => "nul.0";
        public byte ProtocolVersion => 0;
        public string ProtocolCode => "nul";
        public int MessageIdSpaceSize => 0;
        public void Init()
        {
        }

        public void HandleMessage(Packet message)
        {
        }

        public void DisconnectProtocol(EthDisconnectReason ethDisconnectReason, string details)
        {
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized
        {
            add { }
            remove { }
        }

        public event EventHandler<ProtocolEventArgs> SubprotocolRequested
        {
            add { }
            remove { }
        }
    }
}
