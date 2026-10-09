// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
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

        await HandleSingleRequest(new JsonRpcConfig(), testLogger);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testLogger.LogList, Has.None.StartsWith("IPC server error"));
            Assert.That(testLogger.LogList, Has.Member("IPC client disconnected."));
        }
    }

    private static readonly string[]?[] IpcEnabledModulesCases = [null, [], ["engine", "eth"]];

    [TestCaseSource(nameof(IpcEnabledModulesCases))]
    public async Task HandleIpcConnection_uses_IpcEnabledModules_for_the_context_url(string[]? ipcEnabledModules)
    {
        JsonRpcConfig config = new() { EnabledModules = ["eth"], IpcEnabledModules = ipcEnabledModules };

        JsonRpcContext? context = await HandleSingleRequest(config, new TestLogger());

        string[]? expectedModules = ipcEnabledModules is { Length: > 0 } ? ipcEnabledModules : null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context?.Url?.EnabledModules.Order(), Is.EqualTo(expectedModules?.Order()));
        }
    }

    /// <summary>
    /// Sends one request over a Unix domain socket and returns the context the processor received.
    /// The processor then fails as if the client dropped mid-response, which ends the connection.
    /// </summary>
    private static async Task<JsonRpcContext?> HandleSingleRequest(IJsonRpcConfig config, TestLogger testLogger)
    {
        JsonRpcContext? context = null;
        IJsonRpcProcessor processor = Substitute.For<IJsonRpcProcessor>();
        processor
            .ProcessAsync(
                Arg.Any<PipeReader>(),
                Arg.Any<JsonRpcContext>(),
                Arg.Any<IJsonRpcResponseSink>(),
                Arg.Any<JsonRpcProcessingOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                context = callInfo.ArgAt<JsonRpcContext>(1);
                return ValueTask.FromException(new ObjectDisposedException(typeof(IpcSocketMessageStream).FullName));
            });

        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IJsonRpcConfig>().Returns(config);

        using JsonRpcIpcRunner runner = new(
            processor,
            configProvider,
            new OneLoggerLogManager(new(testLogger)),
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

        return context;
    }

    [Test]
    public async Task HandleIpcConnection_EngineEnabled_ProcessesPipelinedRequestsInOrder([Values] bool ipcOverride)
    {
        // 1. IPC serves Engine through either the global module list or its own override, with concurrency above 1.
        // 2. The client pipelines a slow request (newPayload), then a fast one (forkchoiceUpdated).
        // 3. Hold the slow request while a second worker has a bounded opportunity to answer the fast one.
        // 4. The fast request must start only after the slow one has answered.
        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelinedRequestRecorder recorder = new(slowGate: releaseSlow.Task);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        string[] enabledModules = ipcOverride ? [ModuleType.Eth] : [ModuleType.Engine, ModuleType.Eth];
        string[]? ipcModules = ipcOverride ? [ModuleType.Engine, ModuleType.Eth] : null;
        await using (IpcConnection connection = await IpcConnection.OpenAsync(recorder.Processor, enabledModules, cts.Token, ipcModules))
        {
            try
            {
                await connection.SendAsync("slow"u8.ToArray(), cts.Token);
                await connection.SendAsync("fast"u8.ToArray(), cts.Token);
                try
                {
                    await recorder.FastAnswered.WaitAsync(TimeSpan.FromSeconds(1), cts.Token);
                }
                catch (TimeoutException)
                {
                    // With one worker, the fast request cannot answer until the slow request is released.
                }
            }
            finally
            {
                releaseSlow.TrySetResult();
            }

            await Task.WhenAll(recorder.SlowAnswered, recorder.FastAnswered).WaitAsync(cts.Token);
        }

        Assert.That(recorder.Events, Is.EqualTo(PipelinedRequestRecorder.InOrder),
            "IPC serving the engine API must run a pipelined request only after the one before it has answered");
    }

    [Test]
    public async Task HandleIpcConnection_EngineDisabled_AnswersAFastRequestBehindASlowOne([Values] bool ipcOverride)
    {
        // 1. IPC excludes Engine through either the global module list or its own override, with concurrency above 1.
        // 2. The client pipelines a slow request, then a fast one; the slow one is held until the fast one has answered.
        // 3. Processing one request at a time would never answer the fast request, so the bounded wait fails.
        TaskCompletionSource releaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelinedRequestRecorder recorder = new(slowGate: releaseSlow.Task);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        string[] enabledModules = ipcOverride ? [ModuleType.Engine, ModuleType.Eth] : [ModuleType.Eth];
        string[]? ipcModules = ipcOverride ? [ModuleType.Eth] : null;
        await using IpcConnection connection = await IpcConnection.OpenAsync(recorder.Processor, enabledModules, cts.Token, ipcModules);
        try
        {
            await connection.SendAsync("slow"u8.ToArray(), cts.Token);
            await connection.SendAsync("fast"u8.ToArray(), cts.Token);
            Assert.That(async () => await recorder.FastAnswered.WaitAsync(cts.Token), Throws.Nothing,
                "a slow request must not block a later request when IPC does not serve the engine API");
        }
        finally
        {
            releaseSlow.TrySetResult();
        }

        await recorder.SlowAnswered.WaitAsync(cts.Token);
    }

    private sealed class IpcConnection : IAsyncDisposable
    {
        private readonly TempPath _path;
        private readonly Socket _listener;
        private readonly Socket _client;
        private readonly IpcSocketMessageStream _sendStream;
        private readonly JsonRpcIpcRunner _runner;
        private readonly Task _serving;

        private IpcConnection(TempPath path, Socket listener, Socket client, JsonRpcIpcRunner runner, Task serving)
        {
            _path = path;
            _listener = listener;
            _client = client;
            _sendStream = new IpcSocketMessageStream(client);
            _runner = runner;
            _serving = serving;
        }

        public static async Task<IpcConnection> OpenAsync(IJsonRpcProcessor processor, string[] enabledModules, CancellationToken token, string[]? ipcModules = null)
        {
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            configProvider.GetConfig<IJsonRpcConfig>().Returns(new JsonRpcConfig { EnabledModules = enabledModules, IpcEnabledModules = ipcModules });
            JsonRpcIpcRunner runner = new(
                processor,
                configProvider,
                LimboLogs.Instance,
                Substitute.For<IJsonRpcLocalStats>(),
                new EthereumJsonSerializer(),
                Substitute.For<IFileSystem>());

            TempPath path = TempPath.GetTempFile();
            UnixDomainSocketEndPoint endPoint = new(path.Path);
            Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(endPoint);
                listener.Listen(1);
                await client.ConnectAsync(endPoint, token);
                Socket server = await listener.AcceptAsync(token);
                Task serving = Task.Run(() => runner.HandleIpcConnection(server, token), CancellationToken.None);
                return new IpcConnection(path, listener, client, runner, serving);
            }
            catch
            {
                client.Dispose();
                listener.Dispose();
                path.Dispose();
                runner.Dispose();
                throw;
            }
        }

        public async Task SendAsync(byte[] message, CancellationToken token)
        {
            await _sendStream.WriteAsync(message, token);
            await _sendStream.WriteEndOfMessageAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _client.Shutdown(SocketShutdown.Send);
                await _serving;
            }
            finally
            {
                await _sendStream.DisposeAsync();
                _client.Dispose();
                _listener.Dispose();
                _path.Dispose();
                _runner.Dispose();
            }
        }
    }
}
