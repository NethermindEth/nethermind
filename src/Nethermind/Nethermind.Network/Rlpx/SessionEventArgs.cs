// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P;

namespace Nethermind.Network.Rlpx
{
    public class SessionEventArgs(ISession session) : EventArgs
    {
        public ISession Session { get; } = session;
    }
}
