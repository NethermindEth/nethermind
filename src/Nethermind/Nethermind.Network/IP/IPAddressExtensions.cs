// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.IP
{
    public static class IPAddressExtensions
    {
        /// <summary>
        /// An extension method to determine if an IP address is internal, as specified in RFC1918
        /// </summary>
        /// <param name="toTest">The IP address that will be tested</param>
        /// <returns>Returns true if the IP is internal, false if it is external</returns>
        public static bool IsInternal(this IPAddress toTest)
        {
            byte[] bytes = toTest.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                172 => bytes[1] < 32 && bytes[1] >= 16,
                192 => bytes[1] == 168,
                _ => false,
            };
        }
    }
}
