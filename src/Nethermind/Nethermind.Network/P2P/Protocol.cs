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
    public static class Protocol 
    {
        /// <summary>
        /// devp2p Wire
        /// </summary>
        public const string P2P = "p2p";
        /// <summary>
        /// Ethereum Wire
        /// </summary>
        public const string Eth = "eth";
        /// <summary>
        /// Whisper
        /// </summary>
        public const string Shh = "shh";
        /// <summary>
        /// Swarm
        /// </summary>
        public const string Bzz = "bzz";
        /// <summary>
        /// Lightweight Clients
        /// </summary>
        public const string Les = "les";
        /// <summary>
        /// Parity Warp Sync
        /// </summary>
        public const string Par = "par";
        /// <summary>
        /// Nethermind Data Marketplace
        /// </summary>
        public const string Ndm = "ndm";
        /// <summary>
        /// Witness
        /// </summary>
        public const string Wit = "wit";
    }
}
