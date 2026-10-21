// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Scheduler;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Proofs;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.LightClient.Test;

public class TransportTests
{
    [Test]
    public async Task Execution_port_configures_both_tcp_and_discovery([Values(1, 30317, 65535)] int port)
    {
        await using ExecutionPeerTransport transport = new("sepolia", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.SepoliaGenesis, Hash256.Zero), 0, port: port);
        INetworkConfig configuration = Services(transport).Resolve<INetworkConfig>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configuration.P2PPort, Is.EqualTo(port));
            Assert.That(configuration.DiscoveryPort, Is.EqualTo(port));
        }
    }

    [Test]
    public async Task Execution_startup_cancels_before_network_binding([Values] bool alreadyCanceled)
    {
        await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0);
        IPResolver resolver = (IPResolver)Services(transport).Resolve<IIPResolver>();
        TaskCompletionSource<IIPResolver.NethermindIp> resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(IPResolver).GetField("_resolveTask", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(resolver, resolution.Task);
        using CancellationTokenSource stop = new();
        if (alreadyCanceled) stop.Cancel();
        Task start = transport.StartAsync(stop.Token);
        try
        {
            if (!alreadyCanceled) Assert.That(start.IsCompleted, Is.False);
            stop.Cancel();
            Assert.That(async () => await start.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(resolution.Task.IsCompleted, Is.False, "Cancellation must not wait for external IP resolution.");
        }
        finally
        {
            resolution.TrySetException(new IOException("Test IP resolution stopped."));
            _ = resolution.Task.Exception;
            try { await start; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    [Test]
    public async Task Production_discovery_storage_restarts_and_revalidates_signed_enrs([Values] bool tamper)
    {
        string directory = PeerTestDirectory();
        try
        {
            using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
            NodeRecord record = new();
            record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
            record.SetEntry(new IpEntry(IPAddress.Parse("8.8.8.8")));
            record.SetEntry(new TcpEntry(30303));
            record.SetEntry(new UdpEntry(30303));
            new NodeRecordSigner(new Ecdsa(), key).Sign(record);
            NetworkNode node = new(record.ToString());
            await using (ExecutionPeerTransport first = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory))
            {
                INetworkStorage storage = Services(first).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes);
                storage.StartBatch();
                storage.UpdateNodes([node]);
                storage.Commit();
            }
            if (tamper)
            {
                node.Enr!.Signature = new Signature(new byte[64], 0);
                string persisted = Convert.ToHexString(key.PublicKey.Bytes) + ","
                    + Convert.ToHexString(NetworkNodeDecoder.Instance.EncodeAsBytes(node)) + Environment.NewLine;
                await File.WriteAllTextAsync(Path.Combine(directory, "el/mainnet/discovery", SimpleFilePublicKeyDb.DbFileName), persisted);
            }
            await using ExecutionPeerTransport resumed = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory);
            NetworkNode[] restored = Services(resumed).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes).GetPersistedNodes();
            if (tamper) Assert.That(restored, Is.Empty);
            else
            {
                Assert.That(restored.Single().NodeId, Is.EqualTo(key.PublicKey));
                Assert.That(restored[0].IsEnr, Is.True);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task Oversized_discovery_cache_is_preserved_and_does_not_block_startup()
    {
        string directory = PeerTestDirectory();
        string cacheDirectory = Path.Combine(directory, "el/mainnet/discovery");
        Directory.CreateDirectory(cacheDirectory);
        string path = Path.Combine(cacheDirectory, SimpleFilePublicKeyDb.DbFileName);
        try
        {
            await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
            await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
                new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0, directory);
            INetworkStorage storage = Services(transport).ResolveKeyed<INetworkStorage>(DbNames.DiscoveryV5Nodes);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storage.GetPersistedNodes(), Is.Empty);
                Assert.That(File.Exists(path), Is.False);
                Assert.That(Directory.GetFiles(cacheDirectory, "*.invalid-*"), Has.Length.EqualTo(1));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IContainer Services(ExecutionPeerTransport transport) => (IContainer)typeof(ExecutionPeerTransport)
        .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

    private static string PeerTestDirectory() => Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts",
        "NethermindLightClientPeerTests", Guid.NewGuid().ToString("N"));

    [Test]
    public void Discovery_snapshot_retains_only_a_bounded_set_of_signed_records()
    {
        using MemDb db = new();
        NetworkStorage storage = new(db, Nethermind.Logging.LimboLogs.Instance);
        BoundedEnrStorage bounded = new(storage);
        NetworkNode[] nodes = new NetworkNode[BoundedEnrStorage.Capacity + 1];
        for (int i = 0; i < nodes.Length; i++)
        {
            using PrivateKey key = new(((UInt256)(i + 1)).ToBigEndian());
            NodeRecord record = new();
            record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
            new NodeRecordSigner(new Ecdsa(), key).Sign(record);
            nodes[i] = new(record.ToString());
        }
        bounded.StartBatch();
        bounded.UpdateNodes(nodes);
        bounded.Commit();
        Assert.That(bounded.GetPersistedNodes(), Has.Length.EqualTo(BoundedEnrStorage.Capacity));
        bounded.StartBatch();
        bounded.UpdateNodes([nodes[^1]]);
        bounded.Commit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bounded.GetPersistedNodes().Single().NodeId, Is.EqualTo(nodes[^1].NodeId));
            Assert.That(new NetworkStorage(db, Nethermind.Logging.LimboLogs.Instance).GetPersistedNodes().Single().NodeId, Is.EqualTo(nodes[^1].NodeId));
        }
    }

    [Test]
    public async Task Relay_pool_interface_serves_verified_blob_sidecars_and_cells()
    {
        await KzgPolynomialCommitments.InitializeAsync(Nethermind.Logging.LimboLogs.Instance.GetClassLogger<TransportTests>());
        IBlobProofsManager proofs = IBlobProofsManager.For(ProofVersion.V1);
        ShardBlobNetworkWrapper wrapper = proofs.AllocateWrapper(new byte[131_072]);
        proofs.ComputeProofsAndCommitments(wrapper);
        Transaction transaction = new()
        {
            Type = TxType.Blob,
            ChainId = 1,
            Nonce = 7,
            GasLimit = 21_000,
            DecodedMaxFeePerGas = 10,
            GasPrice = 2,
            MaxFeePerBlobGas = 2,
            To = Address.Zero,
            BlobVersionedHashes = proofs.ComputeHashes(wrapper),
            NetworkWrapper = wrapper,
        };
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        new EthereumEcdsa(1).Sign(key, transaction);
        transaction.Hash = transaction.CalculateHash();
        using BoundedTransactionRelay relay = new();
        relay.AddVerified(transaction);
        ITxPool pool = relay;
        Assert.That(pool.TryGetPendingTransaction(transaction.Hash, out Transaction? served), Is.True);
        Assert.That(pool.TryGetPendingTransactionWithoutBlobs(transaction.Hash, out Transaction? metadata), Is.True);
        Assert.That(pool.TryGetBlobCellsAndProofsV1(transaction.BlobVersionedHashes[0]!, BlobCellMask.FromIndices([0, 127]),
            out BlobCellMask mask, out byte[][]? cells, out byte[][]? selectedProofs), Is.True);
        ShardBlobNetworkWrapper partial = wrapper with { Blobs = [], CellMask = mask, Cells = cells };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.SupportsBlobs, Is.True);
            Assert.That(pool.IsKnown(transaction.Hash), Is.True);
            Assert.That(served, Is.SameAs(transaction));
            Assert.That(((ShardBlobNetworkWrapper)metadata!.NetworkWrapper!).Blobs, Is.Empty);
            Assert.That(pool.GetPendingBlobTransactionsCount(), Is.EqualTo(1));
            Assert.That(pool.GetPendingTransactionsCount(), Is.Zero);
            Assert.That(BlobCellsHelper.ValidateCells(partial), Is.True);
            Assert.That(selectedProofs, Is.EqualTo(new[] { wrapper.Proofs[0], wrapper.Proofs[127] }));
        }
        Assert.That(pool.RemoveTransaction(transaction.Hash), Is.True);
        Assert.That(pool.TryGetPendingTransaction(transaction.Hash, out _), Is.False);
    }

    [Test]
    public async Task Peer_serving_scheduler_bounds_queue_and_cleans_up_shutdown()
    {
        LightPeerRequestScheduler scheduler = new(Nethermind.Logging.LimboLogs.Instance);
        TaskCompletionSource occupied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int cleaned = 0;
        Task Run(ServingRequest _, CancellationToken token)
        {
            if (Interlocked.Increment(ref active) == 2) occupied.TrySetResult();
            return release.Task.WaitAsync(token);
        }
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run, TimeSpan.FromHours(1)), Is.True);
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run, TimeSpan.FromHours(1)), Is.True);
        await occupied.Task;
        for (int i = 0; i < 32; i++)
            Assert.That(scheduler.TryScheduleTask(new ServingRequest(), (_, token) =>
            {
                Assert.That(token.IsCancellationRequested, Is.True);
                Interlocked.Increment(ref cleaned);
                return Task.CompletedTask;
            }), Is.True);
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run), Is.False);
        await scheduler.DisposeAsync();
        Assert.That(cleaned, Is.EqualTo(32));
        Assert.That(scheduler.TryScheduleTask(new ServingRequest(), Run), Is.False);
    }

    private sealed record ServingRequest : IBackgroundTaskRequest<ServingRequest>;

    [Test]
    public async Task Execution_transport_uses_discv5_without_direct_bootnode_candidates()
    {
        await using ExecutionPeerTransport transport = new("mainnet", Nethermind.Logging.LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.MainnetGenesis, Hash256.Zero), 0);
        IContainer services = Services(transport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(services.Resolve<IDiscoveryConfig>().DiscoveryVersion, Is.EqualTo(DiscoveryVersion.V5));
            Assert.That(services.Resolve<INetworkConfig>().EnableExternalIpResolution, Is.True);
            Assert.That(services.Resolve<IIPResolver>(), Is.TypeOf<IPResolver>());
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
    public async Task Batch_byte_budget_preserves_successes_and_does_not_execute_remaining_items([Values] bool overBoundary)
    {
        int firstTokenBytes = BoundedJson.ResultLimit;
        int envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, result = "" }).Length - 2;
        int secondTokenBytes = RpcEndpoint.BatchResponseLimit - RpcEndpoint.BatchErrorReserve - 3
            - firstTokenBytes - 2 * envelopeBytes + (overBoundary ? 1 : 0);
        using JsonDocument first = JsonDocument.Parse("\"" + new string('a', firstTokenBytes - 2) + "\"");
        using JsonDocument second = JsonDocument.Parse("\"" + new string('b', secondTokenBytes - 2) + "\"");
        using JsonDocument requests = JsonDocument.Parse("""
            [{"jsonrpc":"2.0","method":"first","id":1},
             {"jsonrpc":"2.0","method":"second","id":2},
             {"jsonrpc":"2.0","method":"later","id":"retained"},
             {"jsonrpc":"2.0","method":"notification"},
             {"jsonrpc":"2.0","method":"invalid","id":true}]
            """);
        int executions = 0;
        RecordingLogger logger = new();
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();
        await RpcEndpoint.WriteBatchAsync(context, requests.RootElement, (_, _, _) =>
            Task.FromResult<object>(Interlocked.Increment(ref executions) == 1 ? first.RootElement : second.RootElement),
            logger, CancellationToken.None);
        context.Response.Body.Position = 0;
        using JsonDocument response = await JsonDocument.ParseAsync(context.Response.Body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(executions, Is.EqualTo(2));
            Assert.That(context.Response.Body.Length, Is.LessThanOrEqualTo(RpcEndpoint.BatchResponseLimit));
            Assert.That(response.RootElement.GetArrayLength(), Is.EqualTo(4));
            Assert.That(response.RootElement[0].GetProperty("result").GetString()!.Length, Is.EqualTo(firstTokenBytes - 2));
            if (overBoundary) Assert.That(response.RootElement[1].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            else Assert.That(response.RootElement[1].GetProperty("result").GetString()!.Length, Is.EqualTo(secondTokenBytes - 2));
            Assert.That(response.RootElement[2].GetProperty("id").GetString(), Is.EqualTo("retained"));
            Assert.That(response.RootElement[2].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32005));
            Assert.That(response.RootElement[3].GetProperty("id").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(response.RootElement[3].GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
            Assert.That(logger.Entries, Has.Count.EqualTo(5));
            Assert.That(logger.Entries[1], Does.Contain(overBoundary ? "error -32005" : "-> ok"));
            Assert.That(logger.Entries[3], Does.Contain("RPC notification -> error -32005 in "));
            Assert.That(logger.Entries[4], Does.Contain("RPC invalid -> error -32600 in "));
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
    public async Task Only_single_transaction_submission_can_exceed_the_standard_body_limit([Values] bool transaction)
    {
        string body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = transaction ? "eth_sendRawTransaction" : "eth_chainId",
            padding = new string('x', RpcEndpoint.StandardRequestBodySize),
        });
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance);
        Assert.That(context.Response.StatusCode, Is.EqualTo(transaction ? 200 : StatusCodes.Status413PayloadTooLarge));
        if (transaction)
        {
            context.Response.Body.Position = 0;
            using JsonDocument response = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.That(response.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32601));
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

        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, logger);

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
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        try
        {
            for (int i = 0; i < active.Length; i++)
            {
                bodies[i] = new BlockingBody(release.Task);
                DefaultHttpContext context = new();
                context.Request.Body = bodies[i];
                context.Response.Body = new MemoryStream();
                active[i] = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance);
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
        using VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"],\"id\":1},{\"jsonrpc\":\"2.0\",\"method\":\"eth_chainId\",\"id\":2}]"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance, clock);
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
        using VerifiedRpc rpc = new(source, () => new VerifiedHead(1, 1, Keccak.Zero, Keccak.Zero), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"method\":\"eth_getBalance\",\"params\":[\"0x1234567890123456789012345678901234567890\",\"finalized\"]}"));
        context.Response.Body = new MemoryStream();

        Task pending = RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, NullLogger.Instance, clock);
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
        using VerifiedRpc rpc = new(null!, () => throw new AssertionException("Unexpected head read."), 1);
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request));
        context.Response.Body = new MemoryStream();
        await RpcEndpoint.HandleAsync(context, rpc.InvokeAsync, logger ?? NullLogger.Instance);
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
