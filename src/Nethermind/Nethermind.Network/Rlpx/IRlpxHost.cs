// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx
{
    /// <summary>
    /// Handles a disconnected RLPx session.
    /// </summary>
    /// <param name="sender">The host that observed the disconnect.</param>
    /// <param name="session">The disconnected session.</param>
    /// <param name="args">The disconnect details.</param>
    public delegate void SessionDisconnectedEventHandler(object? sender, ISession session, DisconnectEventArgs args);

    public interface IRlpxHost
    {
        Task Init();
        Task<bool> ConnectAsync(Node node);
        Task Shutdown();
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

        event EventHandler<SessionEventArgs>? SessionCreated;

        /// <summary>
        /// Raised when a tracked session disconnects.
        /// </summary>
        event SessionDisconnectedEventHandler? SessionDisconnected;

        ISessionMonitor SessionMonitor { get; }
    }
}
