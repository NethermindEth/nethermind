// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface ISessionMonitor
    {
        void Start();
        void Stop();
        void AddSession(ISession session);

        /// <summary>
        /// Removes a session from ping monitoring.
        /// </summary>
        /// <param name="session">The session to remove.</param>
        void RemoveSession(ISession session);
        public IEnumerable<ISession> Sessions { get; }
    }
}
