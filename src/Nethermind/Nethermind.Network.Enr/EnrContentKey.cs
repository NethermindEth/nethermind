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
// 

namespace Nethermind.Network.Enr
{
    public static class EnrContentKey
    {
        /// <summary>
        /// ETH info
        /// </summary>
        public const string Eth = "eth";
        
        /// <summary>
        /// Name of identity scheme, e.g. "v4"
        /// </summary>
        public const string Id = "id";

        /// <summary>
        /// IPv4 address, 4 bytes
        /// </summary>
        public const string Ip = "ip";

        /// <summary>
        /// IPv6 address, 16 bytes
        /// </summary>
        public const string Ip6 = "ip6";

        /// <summary>
        /// Compressed secp256k1 public key, 33 bytes
        /// </summary>
        public const string Secp256K1 = "secp256k1";

        /// <summary>
        /// TCP port, big endian integer
        /// </summary>
        public const string Tcp = "tcp";

        /// <summary>
        /// IPv6-specific TCP port, big endian integer
        /// </summary>
        public const string Tcp6 = "tcp6";

        /// <summary>
        /// UDP port, big endian integer
        /// </summary>
        public const string Udp = "udp";

        /// <summary>
        /// IPv6-specific UDP port, big endian integer
        /// </summary>
        public const string Udp6 = "udp6";
    }
}
