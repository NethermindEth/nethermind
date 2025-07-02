// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Sockets
{
    /// <summary>
    /// Interface that provides lower level operations (in comparison to <see cref="ISocketsClient"/>)
    /// from a specific socket implementation like for example WebSockets, UnixDomainSockets or network sockets.
    /// </summary>
    public interface ISocketHandler : IDisposable
    {
        Task SendRawAsync(ArraySegment<byte> data, bool endMessage = true);
        Task<ReceiveResult?> GetReceiveResult(ArraySegment<byte> buffer);
        Task CloseAsync(ReceiveResult? result);
        Stream SendUsingStream();
    }
}
