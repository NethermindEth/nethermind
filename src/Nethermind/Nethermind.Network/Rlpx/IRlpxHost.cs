// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx
{
    public interface IRlpxHost
    {
        Task Init();
        Task ConnectAsync(Node node);
        Task Shutdown();
        PublicKey LocalNodeId { get; }
        int LocalPort { get; }

        event EventHandler<SessionEventArgs> SessionCreated;
    }
}
