// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public class JsonRpcSocketClientTests
{
    private static readonly object _bigObject = BuildRandomBigObject(100_000);

    [Test]
    public async Task Can_Handle_Very_Large_Objects()
    {
        IPEndPoint ipEndPoint = IPEndPoint.Parse("127.0.0.1:1337");

        Task<int> receiveSerialized = Task.Run(async () =>
        {
            using Socket socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(ipEndPoint);
            socket.Listen();

            Socket handler = await socket.AcceptAsync();

            byte[] buffer = new byte[1024];
            int totalRead = 0;

            int read;
            while ((read = await handler.ReceiveAsync(buffer)) != 0)
            {
                totalRead += read;
            }
            return totalRead;
        });

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
                jsonRpcLocalStats: null!,
                jsonSerializer: new EthereumJsonSerializer(converters: new GethLikeTxTraceConverter())
            );
            JsonRpcResult result = JsonRpcResult.Single(
                new JsonRpcSuccessResponse()
                {
                    MethodName = nameof(Can_Handle_Very_Large_Objects), Id = "42", Result = _bigObject
                }, default);

            return await client.SendJsonRpcResult(result);
        });

        await Task.WhenAll(sendJsonRpcResult, receiveSerialized);
        int sent = sendJsonRpcResult.Result;
        int received = receiveSerialized.Result;
        Console.WriteLine($"Sent: ${sent}, Received: ${received}");
        Assert.That(sent, Is.EqualTo(received));
    }

    private static object BuildRandomBigObject(int length)
    {
        string[] strings = BuildRandomStringArray(length);
        return new GethLikeTxTrace()
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = strings, Memory = strings,
                }
            }
        };
    }

    private static string[] BuildRandomStringArray(int length)
    {
        string[] array = new string[length];
        for (int i = 0; i < length; i++)
        {
            array[i] = BuildRandomString(length);
            if (i % 100 == 0)
            {
                GC.Collect();
            }
        }
        return array;
    }

    private static string BuildRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        char[] stringChars = new char[length];
        Random random = new();

        for (int i = 0; i < stringChars.Length; i++)
        {
            stringChars[i] = chars[random.Next(chars.Length)];
        }

        return new string(stringChars);
    }
}
