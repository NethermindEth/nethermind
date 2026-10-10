// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Text;
using System.Text.Json;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Blockchain;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;

namespace Nethermind.LightClient.Test;

public class TransportTests
{
    [Test]
    public async Task Execution_transport_uses_discv5_without_direct_bootnode_candidates()
    {
        await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0);
        IContainer services = (IContainer)typeof(ExecutionPeerTransport)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.Resolve<IDiscoveryConfig>().DiscoveryVersion, Is.EqualTo(DiscoveryVersion.V5));
            Assert.That(services.Resolve<NodesLoaderOptions>().LoadBootnodesAsPeerCandidates, Is.False);
        }

        await foreach (Node node in services.Resolve<NodesLoader>().DiscoverNodes(default))
            Assert.That(node.IsBootnode, Is.False);
    }

    [Test]
    public void Gloas_provisional_execution_status_uses_a_post_merge_block_number_and_current_fork_id()
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), Nethermind.Logging.LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("sepolia.json");
        chainSpec.Genesis!.Header.Hash = KnownHashes.SepoliaGenesis;
        ulong timestamp = BeaconChainSpec.Sepolia.GenesisTime + 11_311_456 * BeaconChainSpec.Sepolia.SecondsPerSlot;
        VerifiedHead provisional = new(11_311_456, 0, Keccak.Compute("authenticated execution header"), Hash256.Zero);
        ExecutionPeerTransport.LightSyncServer server = new(chainSpec, provisional, timestamp);
        ForkInfo forks = new(new ChainSpecBasedSpecProvider(chainSpec), server);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(server.Head!.Number, Is.GreaterThan(1_000_000));
        Assert.That(forks.GetForkId(server.Head.Number, timestamp), Is.EqualTo(forks.GetForkId(uint.MaxValue, timestamp)));
    }

    [Test]
    public void Verified_body_rejects_missing_transactions_or_withdrawals([Values] bool wrongTransactions)
    {
        BlockBody body = new([], [], []);
        BlockHeader header = EmptyHeader();
        if (wrongTransactions) header.TxRoot = Keccak.Compute("unavailable transaction");
        else header.WithdrawalsRoot = Keccak.Compute("unavailable withdrawal");

        Assert.Throws<InvalidDataException>(() => ExecutionPeerTransport.VerifyBlockBody(header, body));
    }

    [Test]
    public void Verified_receipts_reject_inconsistent_root([Values] bool wrongRoot)
    {
        BlockHeader header = EmptyHeader();
        if (wrongRoot) header.ReceiptsRoot = Keccak.Compute("unavailable receipt");

        if (wrongRoot)
            Assert.Throws<InvalidDataException>(() => ExecutionPeerTransport.VerifyReceipts(header, [], MainnetSpecProvider.Instance));
        else
            Assert.DoesNotThrow(() => ExecutionPeerTransport.VerifyReceipts(header, [], MainnetSpecProvider.Instance));
    }

    private static BlockHeader EmptyHeader() => new(Keccak.Zero, UnclesHash.Calculate([]), Address.Zero,
        UInt256.Zero, 10, 30_000_000, 1_700_000_000, [])
    {
        TxRoot = Keccak.EmptyTreeHash,
        ReceiptsRoot = Keccak.EmptyTreeHash,
        WithdrawalsRoot = WithdrawalTrie.CalculateRoot([]),
    };

    [TestCase("{", -32700)]
    [TestCase("[]", -32600)]
    [TestCase("42", -32600)]
    [TestCase("{\"jsonrpc\":2,\"method\":\"eth_chainId\",\"id\":1}", -32600)]
    [TestCase("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":true}", -32600)]
    [TestCase("{\"jsonrpc\":\"2.0\",\"method\":\"eth_sendRawTransaction\",\"id\":1}", -32601)]
    public async Task Rpc_endpoint_rejects_invalid_requests(string request, int code)
    {
        DefaultHttpContext context = await InvokeAsync(request);
        using JsonDocument response = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(code));
    }

    [Test]
    public async Task Rpc_batch_preserves_ids_and_suppresses_notifications()
    {
        DefaultHttpContext context = await InvokeAsync("[{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":\"first\"},{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"},{\"jsonrpc\":\"2.0\",\"method\":\"unknown\",\"id\":null}]");
        using JsonDocument response = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RootElement[0].GetProperty("id").GetString(), Is.EqualTo("first"));
            Assert.That(response.RootElement[0].GetProperty("result").GetString(), Is.EqualTo("0x1"));
            Assert.That(response.RootElement[1].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32601));
        }
    }

    [Test]
    public async Task Rpc_notifications_produce_no_response()
    {
        DefaultHttpContext context = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(204));
            Assert.That(context.Response.Body.Length, Is.Zero);
        }
    }

    [Test]
    public async Task Rpc_requests_log_method_result_and_duration()
    {
        RecordingLogger logger = new();
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"unknown\",\"id\":2}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\"}", logger);
        await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"unknown\\n\\u001bmethod\",\"id\":3}", logger);

        Assert.That(logger.Entries, Has.Count.EqualTo(4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.Entries[0], Does.Match(@"RPC eth_chainId -> ok in \d+(\.\d+)? ms"));
            Assert.That(logger.Entries[1], Does.Contain("RPC unknown -> error -32601 in "));
            Assert.That(logger.Entries[2], Does.Contain("RPC eth_chainId -> notification in "));
            Assert.That(logger.Entries[3], Does.Contain("RPC unknown  method -> error -32601 in "));
        }
    }

    [Test]
    public async Task Rpc_body_above_the_size_limit_is_refused([Values] bool rejectedByServer)
    {
        DefaultHttpContext context = new();
        context.Request.Body = rejectedByServer
            ? new OversizedBody()
            : new MemoryStream(new byte[RpcEndpoint.MaxRequestBodySize + 1]);
        context.Response.Body = new MemoryStream();
        RecordingLogger logger = new();

        await RpcEndpoint.HandleAsync(context, new VerifiedRpc(null!, () => throw new AssertionException("Unexpected head read."), 1), logger);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        Assert.That(logger.Entries, Has.Count.EqualTo(1));
        Assert.That(logger.Entries[0], Does.Contain("body too large"));
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_endpoint_rejects_requests_above_active_limit()
    {
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] active = new Task[64];
        BlockingBody[] bodies = new BlockingBody[active.Length];
        VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        try
        {
            for (int i = 0; i < active.Length; i++)
            {
                bodies[i] = new BlockingBody(release.Task);
                DefaultHttpContext context = new();
                context.Request.Body = bodies[i];
                context.Response.Body = new MemoryStream();
                active[i] = RpcEndpoint.HandleAsync(context, rpc, NullLogger.Instance);
            }

            for (int i = 0; i < bodies.Length; i++)
                await bodies[i].Entered.WaitAsync(TimeSpan.FromSeconds(10));

            DefaultHttpContext rejected = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}");
            Assert.That(rejected.Response.StatusCode, Is.EqualTo(StatusCodes.Status429TooManyRequests));
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(active.Where(static task => task is not null));
            foreach (BlockingBody body in bodies)
                body?.Dispose();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_batch_deadline_returns_gateway_timeout_and_accepts_next_request()
    {
        WaitingExecutionSource source = new();
        ManualDeadlineClock clock = new();
        VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"],\"id\":1},{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":2}]"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc, NullLogger.Instance, clock);
        await source.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(90)));
        clock.Expire();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        DefaultHttpContext next = await InvokeAsync("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":3}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status504GatewayTimeout));
            Assert.That(context.Response.Body.Length, Is.Zero);
            Assert.That(next.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task Rpc_notification_completing_after_deadline_returns_gateway_timeout()
    {
        WaitingExecutionSource source = new(ignoreCancellation: true);
        ManualDeadlineClock clock = new();
        VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"]}"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc, NullLogger.Instance, clock);
        await source.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Expire();
        source.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status504GatewayTimeout));
            Assert.That(context.Response.Body.Length, Is.Zero);
        }
    }

    private static async Task<DefaultHttpContext> InvokeAsync(string request, ILogger? logger = null)
    {
        VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        context.Response.Body = new MemoryStream();
        await RpcEndpoint.HandleAsync(context, rpc, logger ?? NullLogger.Instance);
        return context;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }

    private sealed class OversizedBody : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
    }

    private sealed class BlockingBody(Task release) : Stream
    {
        private readonly MemoryStream _content = new(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":1}"));
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult(true);
            await release.WaitAsync(cancellationToken);
            return await _content.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _content.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class WaitingExecutionSource(bool ignoreCancellation = false) : IExecutionStateSource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Account> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Complete() => _result.TrySetResult(new Account(1));

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _result.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new NoopTimer();
        }

        public void Expire() => _callback!(_state);

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }

}
