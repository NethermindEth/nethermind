// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Nethermind.Sockets
{
    //public class IpcSocketsHandler : ISocketHandler
    //{
    //    private readonly IpcSocket _socket;

    //    public IpcSocketsHandler(Socket socket)
    //    {
    //        _socket = socket;
    //    }

    //    public Task SendRawAsync(ArraySegment<byte> data, bool endOfMessage) =>
    //        !_socket.Connected
    //            ? Task.CompletedTask
    //            : _socket.SendAsync(data, SocketFlags.None);

       

    //    public Stream SendUsingStream()
    //    {
    //        return new NetworkStream(_socket, FileAccess.Write, ownsSocket: false);
    //    }

    //    public void Dispose()
    //    {
    //        _socket.Dispose();
    //    }
    //}
}
