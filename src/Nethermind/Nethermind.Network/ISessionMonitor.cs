// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;

namespace Nethermind.Network
{
    public interface ISessionMonitor
    {
        void Start();
        void Stop();
        void AddSession(ISession session);
    }
}
