// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Sockets
{
    /// <summary>
    /// Interface that defines logic (higher level operations) behind a socket communication not linked to any socket implementation like WebSockets or network sockets
    /// The lower level communication is provided by implementing <see cref="ISocketHandler"/>.
    /// </summary>
    public interface ISocketsClient : IDisposable
    {
        string Id { get; }
        string ClientName { get; }
        Task ReceiveAsync();
        Task SendAsync(SocketsMessage message);
    }
}
