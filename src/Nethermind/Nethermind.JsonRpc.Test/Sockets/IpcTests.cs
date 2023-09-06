// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Sockets;

class IpcTests
{
    [Test]
    [Explicit("Takes too long to run")]
    public async Task Can_handle_very_large_objects()
    {
        IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

        Task<int> receiveBytes = OneShotServer(
            ipEndPoint,
            async socket => await CountNumberOfBytes(socket)
        );

        Task<int> sendJsonRpcResult = Task.Run(async () =>
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ipEndPoint);

            ISocketHandler handler = new IpcSocketsHandler(socket);
            JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                clientName: "TestClient",
                handler: handler,
                endpointType: RpcEndpoint.IPC,
                jsonRpcProcessor: null!,
                jsonRpcService: null!,
                jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
            );
            JsonRpcResult result = JsonRpcResult.Single(
                new JsonRpcSuccessResponse()
                {
                    MethodName = "mock", Id = "42", Result = _bigObject
                }, default);

            return await client.SendJsonRpcResult(result);
        });

        await Task.WhenAll(sendJsonRpcResult, receiveBytes);
        int sent = sendJsonRpcResult.Result;
        int received = receiveBytes.Result;
        Assert.That(sent, Is.EqualTo(received));
    }

    [Test]
    [TestCase(2)]
    [TestCase(10)]
    [TestCase(50)]
    public async Task Can_send_multiple_messages(int messageCount)
    {
        IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

        Task<int> receiveBytes = OneShotServer(
            ipEndPoint,
            async socket => await CountNumberOfBytes(socket)
        );

        Task<int> sendJsonRpcResult = Task.Run(async () =>
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ipEndPoint);

            ISocketHandler handler = new IpcSocketsHandler(socket);
            JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                clientName: "TestClient",
                handler: handler,
                endpointType: RpcEndpoint.IPC,
                jsonRpcProcessor: null!,
                jsonRpcService: null!,
                jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
            );
            JsonRpcResult result = JsonRpcResult.Single(
                new JsonRpcSuccessResponse()
                {
                    MethodName = "mock", Id = "42", Result = new RandomObject(1000).Get()
                }, default);

            int totalBytesSent = 0;
            for (int i = 0; i < messageCount; i++)
            {
                totalBytesSent += await client.SendJsonRpcResult(result);
            }

            return totalBytesSent;
        });

        await Task.WhenAll(sendJsonRpcResult, receiveBytes);
        int sent = sendJsonRpcResult.Result;
        int received = receiveBytes.Result;
        Assert.That(sent, Is.EqualTo(received));
    }

    [TestCase(2)]
    [TestCase(10)]
    [TestCase(50)]
    public async Task Can_send_collections(int elements)
    {
        IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

        Task<int> receiveBytes = OneShotServer(
            ipEndPoint,
            async socket => await CountNumberOfBytes(socket)
        );

        Task<int> sendCollection = Task.Run(async () =>
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ipEndPoint);

            ISocketHandler handler = new IpcSocketsHandler(socket);
            JsonRpcSocketsClient client = new JsonRpcSocketsClient(
                clientName: "TestClient",
                handler: handler,
                endpointType: RpcEndpoint.IPC,
                jsonRpcProcessor: null!,
                jsonRpcService: null!,
                jsonRpcLocalStats: new NullJsonRpcLocalStats(),
                jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
            );
            JsonRpcResult result = JsonRpcResult.Collection(
                new JsonRpcBatchResult((_, token) =>
                    new RandomAsyncEnumerable<JsonRpcResult.Entry>(
                        10,
                        () => Task.FromResult(
                            new JsonRpcResult.Entry(
                                new JsonRpcSuccessResponse
                                {
                                    MethodName = "mock", Id = "42", Result = new RandomObject(100),
                                }, default
                            )
                        )
                    ).GetAsyncEnumerator(token)
                )
            );

            return await client.SendJsonRpcResult(result);
        });

        await Task.WhenAll(sendCollection, receiveBytes);
        int sent = sendCollection.Result;
        int received = receiveBytes.Result;
        Assert.That(sent, Is.EqualTo(received));
    }

    private static readonly object _bigObject = new RandomObject(100_000);

    private static async Task<T> OneShotServer<T>(IPEndPoint ipEndPoint, Func<Socket, Task<T>> func)
    {
        using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(ipEndPoint);
        socket.Listen();

        Socket handler = await socket.AcceptAsync();

        return await func(handler);
    }

    private static async Task<int> CountNumberOfBytes(Socket socket)
    {
        byte[] buffer = new byte[1024];
        int totalRead = 0;

        int read;
        while ((read = await socket.ReceiveAsync(buffer)) != 0)
        {
            totalRead += read;
        }
        return totalRead;
    }
}
