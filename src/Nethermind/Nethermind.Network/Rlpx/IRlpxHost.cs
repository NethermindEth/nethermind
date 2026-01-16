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
        /// Determines whether the host should attempt to contact a node at the specified IP address.
        /// </summary>
        /// <param name="ip">The IP address of the remote node to evaluate.</param>
        /// <returns><see langword="true"/> if the host should attempt to contact the node; otherwise, <see langword="false"/>.</returns>
        bool ShouldContact(IPAddress ip);

        event EventHandler<SessionEventArgs> SessionCreated;

        ISessionMonitor SessionMonitor { get; }
    }
}
