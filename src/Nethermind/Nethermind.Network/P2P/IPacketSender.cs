// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P
{
    public interface IPacketSender
    {
        int Enqueue<T>(T message) where T : P2PMessage;

        /// <summary>Waits for writability and completion of a bulk message write.</summary>
        ValueTask<int> EnqueueAsync<T>(T message, CancellationToken cancellationToken) where T : P2PMessage;
    }
}
