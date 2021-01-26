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

namespace Nethermind.Network.P2P
{
    public enum SessionState
    {
        /// <summary>
        /// Newly created session object
        /// </summary>
        New = 0,
        
        /// <summary>
        /// RLPx handshake complete
        /// </summary>
        HandshakeComplete = 1,
        
        /// <summary>
        /// P2P Initialized
        /// </summary>
        Initialized = 2,
        
        /// <summary>
        /// Disconnecting all subprotocols (ETH, NDM and so on)
        /// </summary>
        DisconnectingProtocols = 3,
        
        /// <summary>
        /// Disconnecting P2P protocols.
        /// </summary>
        Disconnecting = 4,
        
        /// <summary>
        /// Disconnected.
        /// </summary>
        Disconnected = 5
    }
}
