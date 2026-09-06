// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.JsonRpc;

[Parallelizable(ParallelScope.Self)]
public class JsonRpcIpcRunnerTests
{
    [Test]
    public async Task HandleIpcConnection_logs_disconnect_not_server_error_when_client_drops_mid_response()
    {
        TestLogger testLogger = new();
        ILogManager logManager = new OneLoggerLogManager(new(testLogger));

        IJsonRpcProcessor processor = Substitute.For<IJsonRpcProcessor>();
        processor
            .ProcessAsync(
                Arg.Any<PipeReader>(),
                Arg.Any<JsonRpcContext>(),
                Arg.Any<IJsonRpcResponseSink>(),
                Arg.Any<JsonRpcProcessingOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException(new ObjectDisposedException(typeof(IpcSocketMessageStream).FullName)));

        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IJsonRpcConfig>().Returns(new JsonRpcConfig());

        using JsonRpcIpcRunner runner = new(
            processor,
            configProvider,
            logManager,
            Substitute.For<IJsonRpcLocalStats>(),
            new EthereumJsonSerializer(),
            Substitute.For<IFileSystem>());

        using TempPath tmpPath = TempPath.GetTempFile();
        UnixDomainSocketEndPoint endPoint = new(tmpPath.Path);
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endPoint);
        listener.Listen(1);

        using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(endPoint);
        using Socket server = await listener.AcceptAsync();

        byte[] request = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[],\"id\":1}\n");
        await client.SendAsync(request, SocketFlags.None);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await runner.HandleIpcConnection(server, cts.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testLogger.LogList, Has.None.StartsWith("IPC server error"));
            Assert.That(testLogger.LogList, Has.Member("IPC client disconnected."));
        }
    }
}
