// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Ethereum.Test.Base;

public abstract partial class BlockchainTestBase
{
    /// <summary>
    /// Runs an <see cref="EngineReorgTest"/>: delivers its payloads and forkchoice updates through the JSON-RPC service,
    /// follows the branch of each outcome the client answers with and checks the observations along the way.
    /// A failed step throws an <see cref="AssertionException"/>.
    /// </summary>
    protected async Task<EthereumTestResult> RunEngineReorgTest(EngineReorgTest test)
    {
        Assert.That(test.LoadFailure, Is.Null, $"test data loading failure in {test.Name}");
        _logger.Info($"Running {test.Name}, Network: [{test.Network!.Name}] at {DateTime.UtcNow:HH:mm:ss.ffffff}");

        ISpecProvider specProvider = CreateEngineReorgSpecProvider(test);
        specProvider.UpdateMergeTransitionInfo(0, 0);
        if (test.Network.IsEip4844Enabled || test.NetworkAfterTransition?.IsEip4844Enabled is true)
        {
            await KzgPolynomialCommitments.InitializeAsync();
        }

        IConfigProvider configProvider = new ConfigProvider();
        IFlatDbConfig flatDbConfig = configProvider.GetConfig<IFlatDbConfig>();
        flatDbConfig.Enabled = UseFlatDb;
        flatDbConfig.EnableLongFinality = false;
        IBlocksConfig blocksConfig = configProvider.GetConfig<IBlocksConfig>();
        ApplyPreWarming(blocksConfig, PreWarmingOverride);
        if (ParallelExecutionOverride.HasValue) blocksConfig.ParallelExecution = ParallelExecutionOverride.Value;
        if (ParallelExecutionBatchReadOverride.HasValue) blocksConfig.ParallelExecutionBatchRead = ParallelExecutionBatchReadOverride.Value;
        if (configProvider.GetConfig<IMergeConfig>() is MergeConfig mergeConfig)
        {
            mergeConfig.NewPayloadBlockProcessingTimeout = (int)EngineProcessingTimeout.TotalMilliseconds;
        }

        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddModule(new TestMergeModule())
            .AddSingleton(specProvider)
            .AddSingleton(ComponentLogManagerOverride ?? _logManager)
            .AddSingleton<IRewardCalculator>(NoBlockRewards.Instance)
            .AddSingleton<IWitnessGeneratingBlockProcessingEnvFactory, WitnessGeneratingBlockProcessingEnvFactory>()
            .Build();

        IMainProcessingContext mainProcessingContext = container.Resolve<IMainProcessingContext>();
        IBlockProcessingQueue processingQueue = mainProcessingContext.BlockProcessingQueue;
        processingQueue.Start();
        try
        {
            BlockHeader genesis = ProcessGenesis(test, mainProcessingContext.WorldState, specProvider, container.Resolve<BlockDecoder>(),
                container.Resolve<IBlockTree>(), (BlockchainProcessor)mainProcessingContext.BlockchainProcessor, stopwatch: null);
            EngineReorgRun run = new(test, container.Resolve<IJsonRpcService>(), processingQueue, genesis.Hash!);
            await run.Start(ForkchoiceUpdatedVersion(specProvider.GetSpec(genesis)));
            await run.RunSteps(test.Steps, "");
            return new EthereumTestResult(test.Name, test.ForkName, true);
        }
        finally
        {
            await processingQueue.StopAsync(true);
        }
    }

    /// <remarks>
    /// Unlike <see cref="CreateSpecProvider"/>, genesis runs under the test's fork, as on a client configured for it:
    /// the transaction pool judges a transaction sent before the first block by the rules of the genesis block.
    /// </remarks>
    private static ISpecProvider CreateEngineReorgSpecProvider(EngineReorgTest test)
    {
        List<(ForkActivation Activation, IReleaseSpec Spec)> transitions = [((ForkActivation)0, ChainUtils.ResolveSpec(test.Network, test.ChainId)!)];
        if (test.NetworkAfterTransition is not null)
            transitions.Add((test.TransitionForkActivation!.Value, ChainUtils.ResolveSpec(test.NetworkAfterTransition, test.ChainId)!));

        return new CustomSpecProvider(test.ChainId, test.ChainId, [.. transitions]);
    }

    private static int ForkchoiceUpdatedVersion(IReleaseSpec spec) =>
        spec.IsEip7928Enabled ? EngineApiVersions.Fcu.V4
        : spec.IsEip4844Enabled ? EngineApiVersions.Fcu.V3
        : spec.WithdrawalsEnabled ? EngineApiVersions.Fcu.V2
        : EngineApiVersions.Fcu.V1;

    /// <summary>
    /// The <c>engine_newPayloadV*</c> version that delivers a payload built for the fork of an
    /// <c>engine_getPayloadV*</c> version.
    /// </summary>
    private static int NewPayloadVersionFor(int getPayloadVersion) => getPayloadVersion switch
    {
        EngineApiVersions.GetPayload.V1 => EngineApiVersions.NewPayload.V1,
        EngineApiVersions.GetPayload.V2 => EngineApiVersions.NewPayload.V2,
        EngineApiVersions.GetPayload.V3 => EngineApiVersions.NewPayload.V3,
        EngineApiVersions.GetPayload.V4 or EngineApiVersions.GetPayload.V5 => EngineApiVersions.NewPayload.V4,
        EngineApiVersions.GetPayload.V6 => EngineApiVersions.NewPayload.V5,
        _ => throw new NotSupportedException($"engine_getPayloadV{getPayloadVersion} has no matching engine_newPayload version.")
    };

    /// <summary>
    /// State of one <see cref="EngineReorgTest"/> run: the label of every known block and payload, and the last build.
    /// </summary>
    /// <remarks>
    /// Steps refer to blocks by label: <c>genesis</c>, <c>zero</c> for the zero hash, a fixture block, or a label a
    /// <c>getPayload</c> step bound to a payload the client built.
    /// </remarks>
    private sealed class EngineReorgRun
    {
        /// <summary>How long a build may take to include the pool's transactions, as the reference consumer waits.</summary>
        private static readonly TimeSpan GetPayloadWaitTime = TimeSpan.FromSeconds(1);
        /// <summary>How long a transaction may take to reach the status a fixture prefers before any listed one is accepted.</summary>
        private static readonly TimeSpan TxStatusDeadline = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan TxStatusPollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly EthereumJsonSerializer Serializer = new();

        private readonly IJsonRpcService _rpcService;
        private readonly IBlockProcessingQueue _processingQueue;
        private readonly JsonRpcContext _context = new(RpcEndpoint.Http,
            url: new JsonRpcUrl(Uri.UriSchemeHttp, "localhost", 8551, RpcEndpoint.Http, true, ["engine", "eth"]));
        private readonly Dictionary<string, Hash256> _hashes;
        private readonly Dictionary<string, (string Params, int Version)> _payloads = [];
        private readonly Dictionary<string, long> _numbers = new() { ["genesis"] = 0 };
        private readonly Dictionary<string, string[]> _transactions = [];
        private readonly HashSet<string> _bound = [];
        private (string PayloadId, JsonElement? Attributes)? _build;

        public EngineReorgRun(EngineReorgTest test, IJsonRpcService rpcService, IBlockProcessingQueue processingQueue, Hash256 genesisHash)
        {
            _rpcService = rpcService;
            _processingQueue = processingQueue;
            _hashes = new() { ["genesis"] = genesisHash, ["zero"] = Hash256.Zero };
            foreach ((string label, EngineReorgBlockJson block) in test.ReorgBlocks)
            {
                int version = int.Parse(block.Payload.NewPayloadVersion!);
                AddBlock(label, block.Payload.Params[0]);
                _payloads[label] = ("[" + string.Join(",", block.Payload.Params.Take(NewPayloadParamCounts[version]).Select(static p => p.GetRawText())) + "]", version);
            }
        }

        private void AddBlock(string label, JsonElement executionPayload)
        {
            _hashes[label] = new Hash256(executionPayload.GetProperty("blockHash").GetString()!);
            _numbers[label] = (long)ParseQuantity(executionPayload.GetProperty("blockNumber").GetString()!);
            _transactions[label] = [.. executionPayload.GetProperty("transactions").EnumerateArray().Select(static t => t.GetString()!)];
        }

        public async Task Start(int forkchoiceUpdatedVersion)
        {
            string genesis = _hashes["genesis"].ToString();
            RpcResult forkchoice = await Call($"engine_forkchoiceUpdatedV{forkchoiceUpdatedVersion}",
                $$"""[{"headBlockHash":"{{genesis}}","safeBlockHash":"{{genesis}}","finalizedBlockHash":"{{genesis}}"},null]""");
            Hash256? genesisAtZero = await BlockHashAt("\"0x0\"");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(forkchoice.IsError ? forkchoice.Describe() : forkchoice.Result.GetProperty("payloadStatus").GetProperty("status").GetString(),
                    Is.EqualTo(PayloadStatus.Valid), "initial forkchoice update to genesis");
                Assert.That(genesisAtZero, Is.EqualTo(_hashes["genesis"]), "genesis block hash");
            }
        }

        public async Task RunSteps(IEnumerable<JsonElement> steps, string path)
        {
            int index = 0;
            foreach (JsonElement step in steps)
            {
                string where = $"step {path}{index++} ({step.GetProperty("type").GetString()}{(OptionalString(step, "description") is { } description ? $": {description}" : "")})";
                switch (step.GetProperty("type").GetString())
                {
                    case "newPayload":
                        await NewPayload(step, where, path);
                        break;
                    case "forkchoiceUpdated":
                        await ForkchoiceUpdated(step, where, path);
                        break;
                    case "getPayload":
                        await GetPayload(step, where);
                        break;
                    case "assertHead":
                        await AssertHead(step, where);
                        break;
                    case "assertCanonical":
                        await AssertCanonical(step, where);
                        break;
                    case "assertState":
                        await AssertState(step, where);
                        break;
                    case "assertReceipt":
                        await AssertReceipt(step, where);
                        break;
                    case "assertLogs":
                        await AssertLogs(step, where);
                        break;
                    case "sendRawTransaction":
                        await SendRawTransaction(step, where);
                        break;
                    case "assertTxStatus":
                        await AssertTxStatus(step, where);
                        break;
                    default:
                        Assert.Fail($"{where}: unknown step type");
                        break;
                }
            }
        }

        private async Task NewPayload(JsonElement step, string where, string path)
        {
            string label = step.GetProperty("block").GetString()!;
            Assert.That(_payloads.TryGetValue(label, out (string Params, int Version) payload), Is.True, $"{where}: unknown block {label}");
            RpcResult response = await Call($"engine_newPayloadV{payload.Version}", payload.Params);
            if (!response.IsError)
            {
                // VALID can arrive before the block is committed, and the next step must see it committed.
                await _processingQueue.WaitUntilRemovedAsync(_hashes[label]).AsTask().WaitAsync(EngineProcessingTimeout);
            }

            await FollowOutcome(step, response, response.IsError ? default : response.Result, head: null, where, path);
        }

        private async Task ForkchoiceUpdated(JsonElement step, string where, string path)
        {
            string head = step.GetProperty("head").GetString()!;
            string state = $$"""{"headBlockHash":"{{Hash(head, where)}}","safeBlockHash":"{{Hash(OptionalString(step, "safe") ?? "zero", where)}}","finalizedBlockHash":"{{Hash(OptionalString(step, "finalized") ?? "zero", where)}}"}""";
            bool build = step.TryGetProperty("payloadAttributes", out JsonElement attributes);
            RpcResult response = await Call($"engine_forkchoiceUpdatedV{ParseInt(step.GetProperty("version"))}",
                $"[{state},{(build ? attributes.GetRawText() : "null")}]");
            if (!response.IsError)
            {
                _build = OptionalString(response.Result, "payloadId") is { } payloadId
                    ? (payloadId, build ? attributes : null)
                    : null;
            }

            await FollowOutcome(step, response, response.IsError ? default : response.Result.GetProperty("payloadStatus"), head, where, path);
        }

        /// <summary>Runs the branch of the first outcome in the step's <c>expect</c> list that the response meets.</summary>
        private async Task FollowOutcome(JsonElement step, RpcResult response, JsonElement payloadStatus, string? head, string where, string path)
        {
            foreach (JsonElement outcome in step.GetProperty("expect").EnumerateArray())
            {
                if (!await Meets(outcome, response, payloadStatus, head, where)) continue;

                string id = outcome.GetProperty("id").GetString()!;
                if (step.TryGetProperty("branches", out JsonElement branches) && branches.TryGetProperty(id, out JsonElement branch))
                {
                    await RunSteps(branch.EnumerateArray(), $"{path}{id}/");
                }

                return;
            }

            Assert.Fail($"{where}: the response {response.Describe()} meets none of the expected outcomes {step.GetProperty("expect").GetRawText()}");
        }

        private async Task<bool> Meets(JsonElement outcome, RpcResult response, JsonElement payloadStatus, string? head, string where)
        {
            if (outcome.TryGetProperty("errorCode", out JsonElement errorCode)) return response.ErrorCode == ParseInt(errorCode);
            if (outcome.TryGetProperty("anyError", out JsonElement anyError) && anyError.GetBoolean()) return response.IsError;
            if (response.IsError) return false;

            if (OptionalString(outcome, "status") is { } status && OptionalString(payloadStatus, "status") != status) return false;
            if (OptionalString(outcome, "latestValidHash") is { } latestValidHash && latestValidHash != "any")
            {
                string? actual = OptionalString(payloadStatus, "latestValidHash");
                if (latestValidHash == "null" ? actual is not null : actual is null || new Hash256(actual) != Hash(latestValidHash, where)) return false;
            }

            if (OptionalString(outcome, "validationError") is { } validationError
                && OptionalString(payloadStatus, "validationError") is null != (validationError == "none")) return false;
            if (OptionalString(outcome, "payloadId") is { } payloadId
                && OptionalString(response.Result, "payloadId") is null != (payloadId == "null")) return false;
            if (outcome.TryGetProperty("headMoved", out JsonElement headMoved)
                && (await BlockHashAt("\"latest\"") == Hash(head!, where)) != headMoved.GetBoolean()) return false;

            return true;
        }

        private async Task GetPayload(JsonElement step, string where)
        {
            Assert.That(_build, Is.Not.Null, $"{where}: no forkchoice update started a build");
            (string payloadId, JsonElement? attributes) = _build!.Value;
            int version = ParseInt(step.GetProperty("version"));
            await Task.Delay(GetPayloadWaitTime);
            RpcResult response = await Call($"engine_getPayloadV{version}", $"[\"{payloadId}\"]");
            Assert.That(response.IsError, Is.False, $"{where}: {response.Describe()}");

            JsonElement payload = version == EngineApiVersions.GetPayload.V1 ? response.Result : response.Result.GetProperty("executionPayload");
            string label = step.GetProperty("bind").GetString()!;
            Hash256 blockHash = new(payload.GetProperty("blockHash").GetString()!);
            HashSet<string> transactions = payload.GetProperty("transactions").EnumerateArray().Select(static t => t.GetString()!.ToLowerInvariant()).ToHashSet();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(new Hash256(payload.GetProperty("parentHash").GetString()!), Is.EqualTo(Hash(step.GetProperty("parent").GetString()!, where)), $"{where}: parent of the built payload");
                if (attributes is { } requested)
                {
                    Assert.That(ParseQuantity(payload.GetProperty("timestamp").GetString()!), Is.EqualTo(ParseQuantity(requested.GetProperty("timestamp").GetString()!)), $"{where}: timestamp of the built payload");
                    Assert.That(new Hash256(payload.GetProperty("prevRandao").GetString()!), Is.EqualTo(new Hash256(requested.GetProperty("prevRandao").GetString()!)), $"{where}: prevRandao of the built payload");
                }

                Assert.That(_bound.Where(other => other != label && _hashes[other] == blockHash), Is.Empty, $"{where}: the built payload is already bound");
                foreach (JsonElement tx in step.GetProperty("transactionsInclude").EnumerateArray())
                    Assert.That(transactions, Does.Contain(RawTransaction(tx, where).ToLowerInvariant()), $"{where}: the built payload lacks {tx.GetRawText()}");
                foreach (JsonElement tx in step.GetProperty("transactionsExclude").EnumerateArray())
                    Assert.That(transactions, Does.Not.Contain(RawTransaction(tx, where).ToLowerInvariant()), $"{where}: the built payload has {tx.GetRawText()}");
            }

            int newPayloadVersion = NewPayloadVersionFor(version);
            string[] parameters =
            [
                payload.GetRawText(),
                Serializer.Serialize(BlobVersionedHashes(response.Result)),
                Serializer.Serialize(attributes is { } built ? OptionalString(built, "parentBeaconBlockRoot") : null),
                response.Result.TryGetProperty("executionRequests", out JsonElement requests) ? requests.GetRawText() : "[]"
            ];
            AddBlock(label, payload);
            _bound.Add(label);
            _payloads[label] = ("[" + string.Join(",", parameters.Take(NewPayloadParamCounts[newPayloadVersion])) + "]", newPayloadVersion);
        }

        private static Hash256[] BlobVersionedHashes(JsonElement getPayloadResult)
        {
            if (!getPayloadResult.TryGetProperty("blobsBundle", out JsonElement bundle)) return [];

            return [.. bundle.GetProperty("commitments").EnumerateArray().Select(static commitment =>
            {
                byte[] hash = new byte[Hash256.Size];
                KzgPolynomialCommitments.TryComputeCommitmentHashV1(Bytes.FromHexString(commitment.GetString()!), hash);
                return new Hash256(hash);
            })];
        }

        private async Task AssertHead(JsonElement step, string where)
        {
            foreach (string tag in (string[])["latest", "safe", "finalized"])
            {
                if (OptionalString(step, tag) is { } label)
                    Assert.That(await BlockHashAt($"\"{tag}\""), Is.EqualTo(Hash(label, where)), $"{where}: {tag} block");
            }
        }

        private async Task AssertCanonical(JsonElement step, string where)
        {
            foreach (JsonProperty height in step.GetProperty("blocks").EnumerateObject())
            {
                Hash256? expected = height.Value.ValueKind == JsonValueKind.Null ? null : Hash(height.Value.GetString()!, where);
                Assert.That(await BlockHashAt($"\"{height.Name}\""), Is.EqualTo(expected), $"{where}: canonical block at {height.Name}");
            }
        }

        private async Task AssertState(JsonElement step, string where)
        {
            string at = OptionalString(step, "at") ?? "latest";
            string block = "\"latest\"";
            if (at != "latest")
            {
                Assert.That(_numbers.TryGetValue(at, out long number), Is.True, $"{where}: unknown label {at}");
                block = $"\"0x{number:x}\"";
                Assert.That(await BlockHashAt(block), Is.EqualTo(Hash(at, where)), $"{where}: {at} is not canonical");
            }

            List<string> differences = [];
            foreach (JsonProperty account in step.GetProperty("accounts").EnumerateObject())
            {
                string address = account.Name;
                if (OptionalString(account.Value, "balance") is { } balance)
                    await CompareQuantity(differences, $"{address} balance", balance, "eth_getBalance", $"[\"{address}\",{block}]");
                if (OptionalString(account.Value, "nonce") is { } nonce)
                    await CompareQuantity(differences, $"{address} nonce", nonce, "eth_getTransactionCount", $"[\"{address}\",{block}]");
                if (OptionalString(account.Value, "code") is { } code)
                {
                    RpcResult actual = await Call("eth_getCode", $"[\"{address}\",{block}]");
                    if (actual.IsError || !string.Equals(actual.Result.GetString(), code, StringComparison.OrdinalIgnoreCase))
                        differences.Add($"{address} code: expected {code}, got {actual.Describe()}");
                }

                if (account.Value.TryGetProperty("storage", out JsonElement storage))
                {
                    foreach (JsonProperty slot in storage.EnumerateObject())
                        await CompareQuantity(differences, $"{address} storage {slot.Name}", slot.Value.GetString()!, "eth_getStorageAt", $"[\"{address}\",\"{slot.Name}\",{block}]");
                }
            }

            Assert.That(differences, Is.Empty, $"{where}: state at {at}");
        }

        private async Task CompareQuantity(List<string> differences, string what, string expected, string method, string parameters)
        {
            RpcResult actual = await Call(method, parameters);
            if (actual.IsError || ParseQuantity(actual.Result.GetString()!) != ParseQuantity(expected))
                differences.Add($"{what}: expected {expected}, got {actual.Describe()}");
        }

        private async Task AssertReceipt(JsonElement step, string where)
        {
            JsonElement tx = step.GetProperty("tx");
            RpcResult receipt = await Call("eth_getTransactionReceipt", $"[\"{TransactionHash(tx, where)}\"]");
            Assert.That(receipt.IsError, Is.False, $"{where}: {receipt.Describe()}");

            if (OptionalString(step, "block") is not { } block)
            {
                Assert.That(receipt.Result.ValueKind, Is.EqualTo(JsonValueKind.Null), $"{where}: {tx.GetRawText()} has a receipt {receipt.Describe()}");
                return;
            }

            if (receipt.Result.ValueKind != JsonValueKind.Object)
                Assert.Fail($"{where}: {tx.GetRawText()} has no receipt");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(new Hash256(receipt.Result.GetProperty("blockHash").GetString()!), Is.EqualTo(Hash(block, where)), $"{where}: block of the receipt of {tx.GetRawText()}");
                if (OptionalString(step, "status") is { } status)
                    Assert.That(ParseQuantity(receipt.Result.GetProperty("status").GetString()!), Is.EqualTo(ParseQuantity(status)), $"{where}: status of the receipt of {tx.GetRawText()}");
            }
        }

        private async Task AssertLogs(JsonElement step, string where)
        {
            Dictionary<string, string> filter = new()
            {
                ["fromBlock"] = OptionalString(step, "fromBlock") ?? "earliest",
                ["toBlock"] = OptionalString(step, "toBlock") ?? "latest",
            };
            if (OptionalString(step, "address") is { } address) filter["address"] = address;

            RpcResult logs = await Call("eth_getLogs", $"[{Serializer.Serialize(filter)}]");
            Assert.That(logs.IsError, Is.False, $"{where}: {logs.Describe()}");
            Hash256[] actual = [.. logs.Result.EnumerateArray().Select(static log => new Hash256(log.GetProperty("blockHash").GetString()!)).Order()];
            Hash256[] expected = [.. step.GetProperty("blocks").EnumerateArray().Select(label => Hash(label.GetString()!, where)).Order()];
            Assert.That(actual, Is.EqualTo(expected), $"{where}: blocks of the matching logs");
        }

        private async Task SendRawTransaction(JsonElement step, string where)
        {
            JsonElement tx = step.GetProperty("tx");
            RpcResult response = await Call("eth_sendRawTransaction", $"[\"{RawTransaction(tx, where)}\"]");
            string observed = response.IsError ? "rejected" : "accepted";
            string[] expected = step.TryGetProperty("expect", out JsonElement expect) ? [.. expect.EnumerateArray().Select(static e => e.GetString()!)] : ["accepted"];
            Assert.That(expected, Does.Contain(observed), $"{where}: {tx.GetRawText()} was {observed}: {response.Describe()}");
        }

        /// <remarks>
        /// Polls until the transaction has the first listed status; once <see cref="TxStatusDeadline"/> passes, any
        /// listed status is accepted.
        /// </remarks>
        private async Task AssertTxStatus(JsonElement step, string where)
        {
            JsonElement tx = step.GetProperty("tx");
            string[] expected = [.. step.GetProperty("expect").EnumerateArray().Select(static e => e.GetString()!)];
            string hash = TransactionHash(tx, where).ToString();
            Stopwatch stopwatch = Stopwatch.StartNew();
            string status;
            JsonElement transaction;
            while (true)
            {
                RpcResult response = await Call("eth_getTransactionByHash", $"[\"{hash}\"]");
                Assert.That(response.IsError, Is.False, $"{where}: {response.Describe()}");
                transaction = response.Result;
                status = transaction.ValueKind == JsonValueKind.Null ? "dropped"
                    : OptionalString(transaction, "blockHash") is null ? "pending"
                    : "included";
                if (status == expected[0] || stopwatch.Elapsed >= TxStatusDeadline) break;
                await Task.Delay(TxStatusPollInterval);
            }

            Assert.That(expected, Does.Contain(status), $"{where}: {tx.GetRawText()} is {status}");
            if (status == "included" && OptionalString(step, "includedIn") is { } includedIn)
                Assert.That(new Hash256(transaction.GetProperty("blockHash").GetString()!), Is.EqualTo(Hash(includedIn, where)), $"{where}: block including {tx.GetRawText()}");
        }

        private string RawTransaction(JsonElement tx, string where)
        {
            string block = tx.GetProperty("block").GetString()!;
            int index = tx.TryGetProperty("index", out JsonElement i) ? ParseInt(i) : 0;
            Assert.That(_transactions.TryGetValue(block, out string[]? transactions) && index < transactions.Length, Is.True, $"{where}: unknown transaction {tx.GetRawText()}");
            return transactions![index];
        }

        private Hash256 TransactionHash(JsonElement tx, string where) => Keccak.Compute(Bytes.FromHexString(RawTransaction(tx, where)));

        private Hash256 Hash(string label, string where)
        {
            Assert.That(_hashes.TryGetValue(label, out Hash256? hash), Is.True, $"{where}: unknown label {label}");
            return hash!;
        }

        /// <summary>The hash of the block <c>eth_getBlockByNumber</c> finds, or null when it finds none.</summary>
        private async Task<Hash256?> BlockHashAt(string blockParameter)
        {
            RpcResult block = await Call("eth_getBlockByNumber", $"[{blockParameter},false]");
            Assert.That(block.IsError, Is.False, $"eth_getBlockByNumber({blockParameter}): {block.Describe()}");
            return block.Result.ValueKind == JsonValueKind.Null ? null : new Hash256(block.Result.GetProperty("hash").GetString()!);
        }

        private async Task<RpcResult> Call(string method, string parameters)
        {
            JsonRpcResponse response = await SendRpc(_rpcService, _context, method, parameters);
            if (TryGetRpcError(response, out int errorCode, out string? errorMessage))
                return new RpcResult(default, errorCode, errorMessage);

            object? data = response switch
            {
                IResultWrapper resultWrapper => resultWrapper.Data,
                JsonRpcSuccessResponse success => success.Result,
                _ => throw new AssertionException($"{method} returned unexpected response type {response.GetType().FullName}")
            };
            using JsonDocument document = JsonDocument.Parse(Serializer.Serialize(data));
            return new RpcResult(document.RootElement.Clone(), null, null);
        }

        private static string? OptionalString(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int ParseInt(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number ? value.GetInt32()
            : value.GetString()!.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? (int)ParseQuantity(value.GetString()!)
            : int.Parse(value.GetString()!, CultureInfo.InvariantCulture);

        private static UInt256 ParseQuantity(string value) =>
            value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? new UInt256(Bytes.FromHexString(value), isBigEndian: true) : UInt256.Parse(value);
    }

    private readonly record struct RpcResult(JsonElement Result, int? ErrorCode, string? ErrorMessage)
    {
        public bool IsError => ErrorCode is not null;

        public string Describe() => IsError ? $"error {ErrorCode}: {ErrorMessage}" : Result.GetRawText();
    }
}
