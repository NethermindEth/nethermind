// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;

namespace Nethermind.Network.IP
{
    public static class IPAddressExtensions
    {
        /// <summary>
        /// An extension method to determine if an IP address is internal or otherwise local to the node.
        /// </summary>
        /// <param name="toTest">The IP address that will be tested</param>
        /// <returns>Returns true if the IP is internal, false if it is external</returns>
        public static bool IsInternal(this IPAddress toTest)
            => IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(toTest);
    }
}
