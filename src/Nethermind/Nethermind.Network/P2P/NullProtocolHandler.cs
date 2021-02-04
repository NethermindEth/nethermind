//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
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

        public void DisconnectProtocol(DisconnectReason disconnectReason, string details)
        {
        }

        public event EventHandler<ProtocolInitializedEventArgs> ProtocolInitialized {
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
