// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Logging;
using NUnit.Framework;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using SettlementBatch = Nethermind.Eez.Execution.Settlement.PostBatch;
using WirePostBatch = Nethermind.Eez.Prove.PostBatch;

namespace Nethermind.Eez.Test;

/// <summary>The attester over its gRPC wire, fed the recorded devnet windows as a composer streams them.</summary>
public class AttesterTests
{
    private const string EffectWindow = "captured-devnet-window-468";
    private const string AnchorWindow = StatelessFixtures.Window84;

    /// <summary>The recorded attester's key, a well-known public test key.</summary>
    private static readonly PrivateKey AttesterKey = AttesterServer.AttesterKey;

    [TestCase(EffectWindow, 463, 468, TestName = "SettlingBlockWithEffects")]
    [TestCase(AnchorWindow, 79, 84, TestName = "AnchorOnlyWindow")]
    public async Task Prove_RecordedWindow_ReturnsTheReferenceAttestation(string fixture, int from, int to)
    {
        RecordingLogger logger = new();
        await using AttesterServer server = await AttesterServer.Start(fixture, logger: logger);
        JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");

        ProveResponse response = await server.Prove(Window(fixture, from, to));
        JsonElement[] events = SignedEvents(logger);
        Assert.That(events, Has.Length.EqualTo(1), "only completed signed windows produce upstream proof evidence");
        JsonElement fields = events[0].GetProperty("fields");
        Assert.That((fields.GetProperty("validated_from_block").GetInt32(), fields.GetProperty("validated_to_block").GetInt32(),
            fields.GetProperty("recomputed_public_inputs_hash").GetString()),
            Is.EqualTo((from, to, oracle.GetProperty("public_inputs_hash").GetString())), "upstream state-chaining matches the actual signed height and independently expected digest");

        Assert.That(response.PublicInputsHash.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()));
        if (oracle.TryGetProperty("mined_proof", out JsonElement proof))
        {
            Assert.That(response.Signature.ToByteArray().ToHexString(true), Is.EqualTo(proof.GetString()), "the signature is the proof L1 accepted");
        }
        else
        {
            Assert.That(response.Signature.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("expected_test_signature").GetString()));
        }
    }

    [TestCase(EffectWindow, 463, 468)]
    [TestCase(AnchorWindow, 79, 84)]
    public async Task Prove_L2OnlyProfile_PreservesCapturedProofWhenConsensusContractsAreAbsent(string fixture, int from, int to)
    {
        await using AttesterServer server = await AttesterServer.Start(fixture, skipConsensusLayerCalls: true);
        JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
        ProveResponse response = await server.Prove(Window(fixture, from, to));
        Assert.That(response.PublicInputsHash.ToByteArray().ToHexString(true), Is.EqualTo(oracle.GetProperty("public_inputs_hash").GetString()));
    }

    [TestCaseSource(nameof(RejectedStreams))]
    public async Task Prove_StreamOutsideTheRules_IsRejectedWithTheReferenceStatus(Func<List<ProveChunk>, List<ProveChunk>> mutate, StatusCode code, string message)
    {
        RecordingLogger logger = new();
        await using AttesterServer server = await AttesterServer.Start(EffectWindow, logger: logger);

        RpcException e = (await Assert.ThrowsAsync<RpcException>(() => server.Prove(mutate(Window(EffectWindow, 463, 468)))))!;

        Assert.That(e.StatusCode, Is.EqualTo(code));
        Assert.That(e.Status.Detail, Does.StartWith(message));
        Assert.That(SignedEvents(logger), Is.Empty, "rejected input cannot count as a successful signed window");
    }

    private static JsonElement[] SignedEvents(RecordingLogger logger) => logger.Messages
        .Where(message => message.StartsWith("{\"event_name\":\"eez.proof_signer.window_signed\"", StringComparison.Ordinal))
        .Select(message => JsonSerializer.Deserialize<JsonElement>(message)).ToArray();

    [Test]
    public async Task Prove_WhileAnotherRequestIsActive_IsUnavailable()
    {
        await using AttesterServer server = await AttesterServer.Start(EffectWindow);
        List<ProveChunk> window = Window(EffectWindow, 463, 468);
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> first = server.Client.Prove();
        await first.RequestStream.WriteAsync(window[0]);

        RpcException e = (await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using AsyncClientStreamingCall<ProveChunk, ProveResponse> second = server.Client.Prove();
            await second.RequestStream.CompleteAsync();
            await second.ResponseAsync;
        }))!;

        Assert.That((e.StatusCode, e.Status.Detail), Is.EqualTo((StatusCode.Unavailable, "another Prove request is already active")));
    }

    [Test]
    public async Task Prove_SharedBackendAdmissionIsOccupied_IsUnavailable()
    {
        await using AttesterServer server = await AttesterServer.Start(EffectWindow);
        using BackendAdmission.BackendLease slot = server.Admission.TryAcquire()!;
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => server.Prove([])))!;
        Assert.That((exception.StatusCode, exception.Status.Detail), Is.EqualTo((StatusCode.Unavailable, "another Prove request is already active")),
            "v1 observes the same backend gate used by v2 block execution");
    }

    [Test]
    public async Task Prove_StreamThatStalls_TimesOut()
    {
        await using AttesterServer server = await AttesterServer.Start(EffectWindow, idleTimeout: TimeSpan.FromMilliseconds(300));
        using AsyncClientStreamingCall<ProveChunk, ProveResponse> call = server.Client.Prove();
        await call.RequestStream.WriteAsync(Window(EffectWindow, 463, 468)[0]);

        RpcException e = (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync))!;

        Assert.That((e.StatusCode, e.Status.Detail), Is.EqualTo((StatusCode.DeadlineExceeded, "Prove stream idle timeout")));
    }

    [TestCase(EezSettlementFailure.InvalidPostBatch, StatusCode.InvalidArgument, "invalid PostBatch calldata")]
    [TestCase(EezSettlementFailure.InvalidDaPayload, StatusCode.InvalidArgument, "invalid batch callData")]
    [TestCase(EezSettlementFailure.InternalInvariant, StatusCode.Internal, "validation backend returned invalid output")]
    [TestCase(EezSettlementFailure.Rejected, StatusCode.FailedPrecondition, "settlement validation rejected")]
    public void ToStatus_SettlementFailure_IsTheReferenceStatus(EezSettlementFailure failure, StatusCode code, string message)
    {
        RpcException e = AttestationPipeline.ToStatus(new EezSettlementException(failure, "detail"), [], Nethermind.Core.Test.Builders.Build.A.Block.TestObject);

        Assert.That((e.StatusCode, e.Status.Detail, e.Trailers.Count), Is.EqualTo((code, message, 0)), "the detailed reason is logged, never sent");
    }

    [Test]
    public void ToStatus_RevertedDelivery_CarriesTheEntryToEvict()
    {
        byte[] calldata = StatelessFixtures.ReadPostBatch(EffectWindow);
        SettlementBatch batch = EezCalldata.DecodePostAndVerifyBatch(calldata);

        RpcException e = AttestationPipeline.ToStatus(new EezSettlementException("reverted") { PoisonedEntryIndex = 2 }, calldata,
            Nethermind.Core.Test.Builders.Build.A.Block.TestObject);

        ProveFailure details = ProveFailure.Parser.ParseFrom(e.Trailers.GetValueBytes("grpc-status-details-bin"));
        Assert.That((details.Inbound.EntryIndex, details.Inbound.EntryHash.ToByteArray()), Is.EqualTo(((uint)2, EezCalldata.EntryHash(batch.Entries[2]).ToByteArray())),
            "raw ProveFailure bytes, the entry hashed as abi.encode(entry)");
    }

    [Test]
    public void ToStatus_UnloadableOutboundCall_CarriesTheUserTransactionToEvict()
    {
        Transaction user = Nethermind.Core.Test.Builders.Build.A.Transaction.SignedAndResolved(Nethermind.Core.Test.Builders.TestItem.PrivateKeyA).TestObject;
        Block settling = Nethermind.Core.Test.Builders.Build.A.Block.WithTransactions(user).TestObject;

        RpcException e = AttestationPipeline.ToStatus(new EezSettlementException("missing event") { PoisonedTransactionIndex = 0 }, [], settling);

        ProveFailure details = ProveFailure.Parser.ParseFrom(e.Trailers.GetValueBytes("grpc-status-details-bin"));
        Assert.That((details.Outbound.TransactionIndex, details.Outbound.TransactionHash.ToByteArray()), Is.EqualTo(((uint)0, user.Hash!.BytesToArray())));
    }

    [Test]
    public void CanonicalSize_KnownFields_IsTheProtobufSize()
    {
        foreach (ProveChunk chunk in Window(EffectWindow, 463, 468))
        {
            Assert.That(WindowAssembler.CanonicalSize(chunk), Is.EqualTo(chunk.CalculateSize()));
        }
    }

    [TestCase("identity", TestName = "Prove_Rollup0NativeIdentity_Rejects")]
    [TestCase("provenance", TestName = "Prove_Rollup0NativeProvenance_Rejects")]
    [TestCase("blobs", TestName = "Prove_Rollup0NativeBlobs_Rejects")]
    [TestCase("posting", TestName = "Prove_Rollup0NativePostingContext_Rejects")]
    public async Task Prove_Rollup0NativeFields_RejectsBeforeSigning(string field)
    {
        await using AttesterServer server = await AttesterServer.Start(AnchorWindow);
        List<ProveChunk> chunks = Window(AnchorWindow, 79, 84);
        switch (field)
        {
            case "identity": chunks[0].Header.ExpectedValidationIdentity = ByteString.CopyFrom(new byte[32]); break;
            case "provenance": chunks[0].Header.ParentProvenance = new ParentProvenance(); break;
            case "blobs": chunks[0].Header.PostBatch.Blobs.Add(ByteString.CopyFrom(new byte[32])); break;
            case "posting": chunks[0].Header.PostBatch.PostingContext = new L1PostingContext(); break;
        }

        RpcException exception = (await Assert.ThrowsAsync<RpcException>(() => server.Prove(chunks)))!;

        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.InvalidArgument), "native metadata must never be silently ignored by Rollup0");
    }

    [Test]
    public void CanonicalSize_UnknownFields_ChargesEveryDecodedByte()
    {
        ProveChunk chunk = Window(AnchorWindow, 79, 84)[0];
        byte[] encoded = chunk.ToByteArray();
        byte[] appended = new byte[encoded.Length + 3];
        encoded.CopyTo(appended, 0);
        appended[^3] = 0xA2;
        appended[^2] = 0x06;
        appended[^1] = 0;

        Assert.That(WindowAssembler.CanonicalSize(ProveChunk.Parser.ParseFrom(appended)), Is.EqualTo(appended.Length),
            "unknown protobuf fields cannot bypass native window byte quotas");
    }

    [Test]
    public void KeyStore_ImportedKey_UnlocksAsTheAttester()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"eez-keystore-{Guid.NewGuid():N}");
        string password = Path.Combine(directory, "password.txt");
        Directory.CreateDirectory(directory);
        File.WriteAllText(password, "devnet\n");
        try
        {
            Address imported = AttestationKeyStore.Import(directory, password, "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d\n", LimboLogs.Instance);

            Assert.That(AttestationKeyStore.Unlock(directory, password, imported, LimboLogs.Instance).Address, Is.EqualTo(AttesterKey.Address));
            Assert.Throws<InvalidOperationException>(() => AttestationKeyStore.Unlock(directory, password, Address.Zero, LimboLogs.Instance));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void ChainConfigLoader_BareConfigAndGenesis_LoadTheSameForks()
    {
        string genesis = Path.Combine(Path.GetTempPath(), $"eez-genesis-{Guid.NewGuid():N}.json");
        File.WriteAllText(genesis, $"{{\"config\":{File.ReadAllText(StatelessFixtures.PathOf(EffectWindow, "chain-config.json"))},\"alloc\":{{}},\"gasLimit\":\"0x1c9c380\","
            + "\"difficulty\":\"0x0\",\"nonce\":\"0x0\",\"timestamp\":\"0x0\",\"extraData\":\"0x\",\"coinbase\":\"0x0000000000000000000000000000000000000000\","
            + "\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\"}");
        try
        {
            ISpecProvider bare = ChainConfigLoader.Load(StatelessFixtures.PathOf(EffectWindow, "chain-config.json"));
            ISpecProvider full = ChainConfigLoader.Load(genesis);

            Assert.That((bare.ChainId, full.ChainId), Is.EqualTo((6290UL, 6290UL)));
            Assert.That(full.GetSpec(new ForkActivation(1, 1)).IsEip7702Enabled, Is.EqualTo(bare.GetSpec(new ForkActivation(1, 1)).IsEip7702Enabled));
        }
        finally
        {
            File.Delete(genesis);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChainConfigLoader_ConfiguredCollector_ReachesAttesterSpecs(bool fullGenesis)
    {
        string path = Path.Combine(Path.GetTempPath(), $"eez-collector-{Guid.NewGuid():N}.json");
        JsonNode config = JsonNode.Parse(File.ReadAllText(StatelessFixtures.PathOf(EffectWindow, "chain-config.json")))!;
        config["feeCollector"] = Nethermind.Core.Test.Builders.TestItem.AddressD.ToString();
        JsonNode document = fullGenesis ? new JsonObject
        {
            ["config"] = config,
            ["alloc"] = new JsonObject(),
            ["gasLimit"] = "0x1c9c380",
            ["difficulty"] = "0x0",
            ["nonce"] = "0x0",
            ["timestamp"] = "0x0",
            ["extraData"] = "0x",
            ["coinbase"] = Address.Zero.ToString(),
            ["mixHash"] = Keccak.Zero.ToString(),
        } : config;
        File.WriteAllText(path, document.ToJsonString());
        try
        {
            ISpecProvider provider = ChainConfigLoader.Load(path);
            Assert.That(provider.GenesisSpec.FeeCollector, Is.EqualTo(Nethermind.Core.Test.Builders.TestItem.AddressD));
            Assert.That(provider.GetSpec(new ForkActivation(1, 1)).FeeCollector, Is.EqualTo(Nethermind.Core.Test.Builders.TestItem.AddressD));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCaseSource(nameof(InvalidSettings))]
    public void Parse_InvalidSetting_Throws(string[] args, string error)
    {
        System.CommandLine.RootCommand root = [];
        AttesterCommandLine.AddTo(root);

        Assert.That(Assert.Throws<FormatException>(() => AttesterCommandLine.Parse(root.Parse([.. ValidArgs().Where(valid => !args.Any(arg => arg.Split('=')[0] == valid.Split('=')[0])), .. args])))!.Message,
            Does.Contain(error));
    }

    [Test]
    public void Parse_ValidSettings_ReadsEverySetting()
    {
        System.CommandLine.RootCommand root = [];
        AttesterCommandLine.AddTo(root);

        AttesterOptions options = AttesterCommandLine.Parse(root.Parse(ValidArgs()));

        Assert.That((options.ListenAddress, options.RollupId, options.BlockTimeSeconds, options.GasLimit, options.Limits),
            Is.EqualTo((IPEndPoint.Parse("0.0.0.0:50061"), 1UL, 2UL, 30_000_000UL, WindowLimits.Default)));
    }

    [TestCase(null, TestName = "Parse_StreamingConfigAbsent_DefaultsToNull")]
    [TestCase("streaming.json", TestName = "Parse_StreamingConfigExplicit_ReadsPath")]
    public void Parse_StreamingConfig_ReadsExplicitPathOrDefaultsToNull(string? path)
    {
        System.CommandLine.RootCommand root = [];
        AttesterCommandLine.AddTo(root);
        string[] args = path is null ? ValidArgs() : [.. ValidArgs(), "--streaming-config=" + path];
        AttesterOptions options = AttesterCommandLine.Parse(root.Parse(args));
        Assert.That(options.StreamingConfigPath, Is.EqualTo(path), "v2 activation must reach production startup from the CLI");
    }

    [TestCase(null, false)]
    [TestCase("true", true)]
    [TestCase("false", false)]
    public void Parse_ConsensusSystemCalls_RequiresExplicitSelection(string? selection, bool expected)
    {
        System.CommandLine.RootCommand root = [];
        AttesterCommandLine.AddTo(root);
        string[] args = selection is null ? ValidArgs() : [.. ValidArgs(), "--skip-consensus-layer-calls=" + selection];
        Assert.That(AttesterCommandLine.Parse(root.Parse(args)).SkipConsensusLayerCalls, Is.EqualTo(expected));
    }

    [Test]
    public void Parse_SystemIdentity_MatchesTheSelectedGenesis([Values] bool native,
        [Values("default", "matching", "other", "zero")] string selection)
    {
        string path = native ? StatelessFixtures.PathOf("rollupx/native-offline-v6", "l2-genesis.json")
            : "genesis.json";
        EezChainRules rules = native ? ChainConfigLoader.LoadProfile(path).Rules : EezChainRules.Rollup0;
        System.CommandLine.RootCommand root = [];
        AttesterCommandLine.AddTo(root);
        string[] args = [.. ValidArgs().Where(arg => !arg.StartsWith("--chain-config=", StringComparison.Ordinal)), "--chain-config=" + path];
        Address address = selection switch
        {
            "matching" => rules.SystemAddress,
            "other" => native ? EezConstants.SystemAddress : Address.SystemUser,
            _ => Address.Zero,
        };
        if (selection != "default") args = [.. args, "--l2-system-address=" + address];
        if (selection is "other" or "zero")
            Assert.That(() => AttesterCommandLine.Parse(root.Parse(args), rules), Throws.TypeOf<FormatException>());
        else
            Assert.That(AttesterCommandLine.Parse(root.Parse(args), rules).RollupId, Is.EqualTo(1UL));
    }

    private static string[] ValidArgs() =>
    [
        "--listen-addr=0.0.0.0:50061", "--chain-config=genesis.json", "--rollup-id=1", $"--vkey=0x{new string('0', 24)}70997970c51812dc3a010c7d01b50e0d17dc79c8",
        "--attester-address=0x70997970C51812dc3A010C7d01b50e0d17dc79C8", "--proof-system=0xe7f1725e7734ce288f8367e1bb143e90bb3f0512", "--password-file=password.txt",
        "--l2-block-time-secs=2",
    ];

    private static TestCaseData[] InvalidSettings() =>
    [
        new(new[] { "--skip-consensus-layer-calls=auto" }, "must be true or false") { TestName = "InvalidConsensusSystemCalls" },
        new(new[] { "--rollup-id=0" }, "--rollup-id must be a positive integer") { TestName = "ZeroRollupId" },
        new(new[] { $"--vkey=0x{new string('0', 64)}" }, "vkey must be non-zero") { TestName = "ZeroVerificationKey" },
        new(new[] { $"--proof-system=0x{new string('0', 40)}" }, "proof-system address must be non-zero") { TestName = "ZeroProofSystem" },
        new(new[] { $"--l2-system-address=0x{new string('0', 40)}" }, "reserved EEZ system address") { TestName = "OtherSystemAddress" },
        new(new[] { "--l2-block-time-secs=0" }, "--l2-block-time-secs must be a positive integer") { TestName = "ZeroBlockTime" },
    ];

    private static TestCaseData[] RejectedStreams() =>
    [
        Case(static w => [], StatusCode.InvalidArgument, "window: empty Prove stream", "EmptyStream"),
        Case(static w => w[1..], StatusCode.InvalidArgument, "window: first chunk must be the window header, got block 463", "BlockBeforeHeader"),
        Case(static w => [w[0], w[0]], StatusCode.InvalidArgument, "window: duplicate header chunk", "DuplicateHeader"),
        Case(static w => [WithHeader(w[0], static h => h.RollupId = 2), .. w[1..]], StatusCode.FailedPrecondition, "window rollup identity rejected", "OtherRollup"),
        Case(static w => [WithHeader(w[0], static h => h.PostBatch = null), .. w[1..]], StatusCode.InvalidArgument, "window: header carries no post_batch", "NoPostBatch"),
        Case(static w => [WithHeader(w[0], static h => h.PostBatch.L1BlockHash = ByteString.CopyFrom(new byte[32])), .. w[1..]], StatusCode.InvalidArgument,
            "window: header post_batch carries a 32-byte l1_block_hash; it must be empty", "BlockBoundBatch"),
        Case(static w => [WithHeader(w[0], static h => h.FromBlock = 0), .. w[1..]], StatusCode.InvalidArgument, "window: invalid window bounds 0..=468", "ZeroFrom"),
        Case(static w => [WithHeader(w[0], static h => h.ToBlock = 5000), .. w[1..]], StatusCode.ResourceExhausted, "window quota: window 463..=5000 spans 4538 blocks, limit is 512",
            "SpanOverLimit"),
        Case(static w => [w[0], w[2], .. w[3..]], StatusCode.InvalidArgument, "window: expected block 463 at block index 0, got 464", "GapInBlocks"),
        Case(static w => [.. w, w[^1]], StatusCode.InvalidArgument, "window: window already carries its declared 6 blocks", "ExtraBlock"),
        Case(static w => w[..^1], StatusCode.InvalidArgument, "window: window 463..=468 expects 6 blocks, stream ended after 5", "MissingBlock"),
        Case(static w => [w[0], w[1], WithBlock(w[2], static b => b.ParentHash = ByteString.CopyFrom(new byte[32])), .. w[3..]], StatusCode.InvalidArgument,
            "window: hash-chain break at block 464", "BrokenHashChain"),
        Case(static w => [w[0], WithBlock(w[1], static b => b.Hash = ByteString.CopyFrom(new byte[31])), .. w[2..]], StatusCode.InvalidArgument,
            "window: block 463 has a 31-byte block hash", "ShortHash"),
        Case(static w => [w[0], WithBlock(w[1], static b => b.Witness = null), .. w[2..]], StatusCode.InvalidArgument, "window: block 463 carries no execution witness",
            "NoWitness"),
        Case(static w => [w[0], new ProveChunk(), .. w[1..]], StatusCode.InvalidArgument, "window: chunk at index 1 carries no kind", "ChunkWithoutKind"),
        Case(static w => [.. w[..^1], WithBlock(w[^1], static b => b.Hash = ByteString.CopyFrom(Keccak.OfAnEmptyString.Bytes))], StatusCode.FailedPrecondition,
            "window validation rejected", "SealedHashDiffers"),
        Case(static w => [WithHeader(w[0], static h => h.PostBatch.AbiCalldata = ByteString.CopyFrom(Truncate(h.PostBatch.AbiCalldata))), .. w[1..]], StatusCode.InvalidArgument,
            "invalid PostBatch calldata", "TruncatedCalldata"),
    ];

    private static byte[] Truncate(ByteString calldata) => calldata.ToByteArray()[..^1];

    private static TestCaseData Case(Func<List<ProveChunk>, List<ProveChunk>> mutate, StatusCode code, string message, string name) =>
        new(mutate, code, message) { TestName = name };

    private static ProveChunk WithHeader(ProveChunk chunk, Action<ProveHeader> mutate)
    {
        ProveChunk copy = chunk.Clone();
        mutate(copy.Header);
        return copy;
    }

    private static ProveChunk WithBlock(ProveChunk chunk, Action<BlockWitness> mutate)
    {
        ProveChunk copy = chunk.Clone();
        mutate(copy.Block);
        return copy;
    }

    /// <summary>The window as the composer streams it: the header, then each block with its witness.</summary>
    internal static List<ProveChunk> Window(string fixture, int from, int to)
    {
        JsonElement oracle = StatelessFixtures.ReadJson(fixture, "oracle.json");
        List<ProveChunk> chunks =
        [
            new ProveChunk
            {
                Header = new ProveHeader
                {
                    RollupId = oracle.GetProperty("rollup_id").GetUInt64(),
                    FromBlock = (ulong)from,
                    ToBlock = (ulong)to,
                    PostBatch = new WirePostBatch { AbiCalldata = ByteString.CopyFrom(StatelessFixtures.ReadPostBatch(fixture)) },
                },
            },
        ];
        foreach (JsonElement block in StatelessFixtures.ReadJson(fixture, "blocks.json").EnumerateArray())
        {
            int number = block.GetProperty("number").GetInt32();
            JsonElement witness = StatelessFixtures.ReadJson(fixture, $"witness-{number}.json");
            chunks.Add(new ProveChunk
            {
                Block = new BlockWitness
                {
                    Number = (ulong)number,
                    Hash = Bytes32(block.GetProperty("hash").GetString()!),
                    ParentHash = Bytes32(block.GetProperty("parent_hash").GetString()!),
                    Rlp = ByteString.CopyFrom(StatelessFixtures.ReadBlock(fixture, $"block-{number}.rlp.hex")),
                    Witness = new ExecutionWitness
                    {
                        State = { Items(witness, "state") },
                        Codes = { Items(witness, "codes") },
                        Keys = { Items(witness, "keys") },
                        Headers = { Items(witness, "headers") },
                    },
                },
            });
        }

        return chunks;
    }

    private static ByteString Bytes32(string hex) => ByteString.CopyFrom(Bytes.FromHexString(hex));

    private static IEnumerable<ByteString> Items(JsonElement witness, string property) =>
        witness.GetProperty(property).EnumerateArray().Select(static item => ByteString.CopyFrom(Bytes.FromHexString(item.GetString()!)));

    private sealed class RecordingLogger : ILogger<ProverService>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }
}
