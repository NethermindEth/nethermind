// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx
{
    public interface IRlpxHost
    {
        Task Init();
        Task<bool> ConnectAsync(Node node);
        Task Shutdown();
        PublicKey LocalNodeId { get; }
        int LocalPort { get; }

        /// <summary>
        /// Determines whether the host should attempt to contact a node at the specified IP address
        /// and records the evaluation in the underlying contact filter.
        /// Calling this method may update internal state and affect the outcome of future
        /// <see cref="ShouldContact(System.Net.IPAddress, bool)"/> calls for the same or other addresses.
        /// </summary>
        /// <param name="ip">The IP address of the remote node to evaluate and record.</param>
        /// <param name="exactOnly">When <see langword="true"/>, only match the exact IP address (bypass subnet bucketing).
        /// Used for static peers and boot nodes that must always be reachable but should not reconnect to the same IP.</param>
        /// <returns>
        /// <see langword="true"/> if the host should attempt to contact the node and the attempt is
        /// accepted by the internal filter; otherwise, <see langword="false"/>.
        /// </returns>
        bool ShouldContact(IPAddress ip, bool exactOnly = false);

        event EventHandler<SessionEventArgs> SessionCreated;

        ISessionMonitor SessionMonitor { get; }
    }
}
