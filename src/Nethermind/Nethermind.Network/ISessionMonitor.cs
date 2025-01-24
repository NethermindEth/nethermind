// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface ISessionMonitor
    {
        void Start();
        void Stop();
        void AddSession(ISession session);
        public ConcurrentDictionary<Guid, ISession> Sessions { get; }
    }
}
