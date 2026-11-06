// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.LightClient.Consensus;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class FuluFixtureTests
{
    private static readonly Hash256 Checkpoint = new("0x4fda428c7ca70ced8ecd846de2a37a339f0518260f848dc5d5d42a4e47b7b3ba");

    [Test]
    public void Captured_bootstrap_and_finality_update_authenticate([Values("mainnet", "hoodi")] string network)
    {
        Capture capture = Fixture(network);
        using JsonDocument bootstrapJson = Read("bootstrap", network);
        using JsonDocument finalityJson = Read("finality", network);
        Assert.That(bootstrapJson.RootElement.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(finalityJson.RootElement.GetProperty("version").GetString(), Is.EqualTo("fulu"));

        LightClientBootstrap bootstrap = Bootstrap(bootstrapJson.RootElement.GetProperty("data"));
        LightClientFinalityUpdate finality = Finality(finalityJson.RootElement.GetProperty("data"));
        Assert.That(SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), Is.EqualTo(capture.Checkpoint));

        BeaconChainSpec spec = capture.Spec;
        LightClientBootstrap decodedBootstrap = LightClientWireCodec.DecodeBootstrap(LightClientWireCodec.EncodeBootstrap(bootstrap, spec), spec);
        LightClientFinalityUpdate decodedFinality = LightClientWireCodec.DecodeFinality(LightClientWireCodec.EncodeFinality(finality, spec), spec);
        LightClientStore store = new(spec, capture.Checkpoint, decodedBootstrap, decodedFinality.SignatureSlot);
        store.Process(decodedFinality, decodedFinality.SignatureSlot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(capture.FinalizedSlot));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(capture.OptimisticSlot));
            Assert.That(store.FinalizedHeader.Execution!.BlockHash, Is.EqualTo(new Hash256(capture.ExecutionHash)));
        }
    }

    [Test]
    public void Captured_bootstrap_rejects_invalid_proofs_or_a_different_anchor(
        [Values("mainnet", "hoodi")] string network,
        [Values("committee-proof", "execution-proof", "checkpoint")] string mutation)
    {
        Capture capture = Fixture(network);
        using JsonDocument bootstrapJson = Read("bootstrap", network);
        LightClientBootstrap bootstrap = Bootstrap(bootstrapJson.RootElement.GetProperty("data"));
        Hash256 checkpoint = capture.Checkpoint;
        switch (mutation)
        {
            case "committee-proof": bootstrap.CurrentSyncCommitteeBranch![0] = Hash256.Zero; break;
            case "execution-proof": bootstrap.Header!.ExecutionBranch![0] = Hash256.Zero; break;
            case "checkpoint": checkpoint = Hash256.Zero; break;
        }

        Assert.That(() => new LightClientStore(capture.Spec, checkpoint, bootstrap, capture.OptimisticSlot + 1),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Captured_finality_rejects_forgery_without_publishing_it(
        [Values("mainnet", "hoodi")] string network,
        [Values("finality-proof", "signature", "signing-network")] string mutation)
    {
        Capture capture = Fixture(network);
        using JsonDocument bootstrapJson = Read("bootstrap", network);
        using JsonDocument finalityJson = Read("finality", network);
        LightClientBootstrap bootstrap = Bootstrap(bootstrapJson.RootElement.GetProperty("data"));
        LightClientFinalityUpdate finality = Finality(finalityJson.RootElement.GetProperty("data"));
        BeaconChainSpec spec = capture.Spec;
        switch (mutation)
        {
            case "finality-proof": finality.FinalityBranch![0] = Hash256.Zero; break;
            case "signature":
                byte[] signature = finality.SyncAggregate!.SyncCommitteeSignature.Bytes.ToArray();
                signature[32] ^= 1;
                finality.SyncAggregate.SyncCommitteeSignature = new BlsSignature(signature);
                break;
            case "signing-network": spec = spec with { GenesisValidatorsRoot = Hash256.Zero }; break;
        }
        LightClientStore store = new(spec, capture.Checkpoint, bootstrap, finality.SignatureSlot);

        Assert.That(() => store.Process(finality, finality.SignatureSlot), Throws.TypeOf<InvalidDataException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(capture.FinalizedSlot));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(capture.FinalizedSlot));
            Assert.That(store.CurrentMaxParticipants, Is.Zero);
        }
    }

    [Test]
    public async Task Captured_authenticated_heads_and_participation_survive_journal_replay([Values("mainnet", "hoodi")] string network)
    {
        Capture capture = Fixture(network);
        using JsonDocument bootstrapJson = Read("bootstrap", network);
        using JsonDocument finalityJson = Read("finality", network);
        LightClientBootstrap bootstrap = Bootstrap(bootstrapJson.RootElement.GetProperty("data"));
        LightClientFinalityUpdate finality = Finality(finalityJson.RootElement.GetProperty("data"));
        LightClientStore store = new(capture.Spec, capture.Checkpoint, bootstrap, finality.SignatureSlot);
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "NethermindLightClientFixtureJournal", Guid.NewGuid().ToString("N"));
        VerifiedConsensusJournal journal = new(directory, capture.Spec, network, capture.Checkpoint, NullLogger.Instance);
        try
        {
            await journal.InitializeAsync(bootstrap, store, CancellationToken.None);
            store.Process(finality, finality.SignatureSlot);
            await journal.AppendAsync(finality, store, CancellationToken.None);
            VerifiedConsensusJournal reader = new(directory, capture.Spec, network, capture.Checkpoint, NullLogger.Instance);
            LightClientStore? resumed = await reader.LoadAsync(finality.SignatureSlot, CancellationToken.None);
            Assert.That(resumed, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(resumed!.FinalizedHeader.Execution!.BlockHash, Is.EqualTo(store.FinalizedHeader.Execution!.BlockHash));
                Assert.That(resumed.OptimisticHeader.Beacon!.Slot, Is.EqualTo(capture.OptimisticSlot));
                Assert.That(resumed.OptimisticHeader.Execution!.BlockHash, Is.EqualTo(store.OptimisticHeader.Execution!.BlockHash));
                Assert.That(resumed.CurrentMaxParticipants, Is.EqualTo(store.CurrentMaxParticipants));
                Assert.That(resumed.Period, Is.EqualTo(store.Period));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record Capture(BeaconChainSpec Spec, Hash256 Checkpoint, ulong FinalizedSlot, ulong OptimisticSlot, string ExecutionHash);

    private static Capture Fixture(string network) => network switch
    {
        "mainnet" => new(BeaconChainSpec.Mainnet, Checkpoint, 15384736, 15384813,
            "0xaf43d16d79770a96dbe621e0ae711cabb98319b5fa1a260ea22080947b48fb0a"),
        "hoodi" => new(BeaconChainSpec.Hoodi, new Hash256("0x5d5f221a04833992d266353bebe3d515dcd7ca9d4afa5550733f978eac535f51"), 4130560, 4130643,
            "0x55a9432f7d806f4532dbcc2471da8b24ba2ab16a9ab7c5d51b482d01db95f527"),
        _ => throw new ArgumentException("Unknown captured network.", nameof(network)),
    };

    private static JsonDocument Read(string name, string network) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Fulu", network == "hoodi" ? "Hoodi" : "", name + ".json")));

    private static LightClientBootstrap Bootstrap(JsonElement json) => new()
    {
        Header = Header(json.GetProperty("header")),
        CurrentSyncCommittee = Committee(json.GetProperty("current_sync_committee")),
        CurrentSyncCommitteeBranch = Branch(json, "current_sync_committee_branch"),
    };

    private static LightClientFinalityUpdate Finality(JsonElement json)
    {
        JsonElement aggregate = json.GetProperty("sync_aggregate");
        return new()
        {
            AttestedHeader = Header(json.GetProperty("attested_header")),
            FinalizedHeader = Header(json.GetProperty("finalized_header")),
            FinalityBranch = Branch(json, "finality_branch"),
            SyncAggregate = new SyncAggregate
            {
                SyncCommitteeBits = new BitArray(Hex(aggregate, "sync_committee_bits")),
                SyncCommitteeSignature = new BlsSignature(Hex(aggregate, "sync_committee_signature")),
            },
            SignatureSlot = Number(json, "signature_slot"),
        };
    }

    private static LightClientHeader Header(JsonElement json)
    {
        JsonElement beacon = json.GetProperty("beacon");
        JsonElement execution = json.GetProperty("execution");
        return new()
        {
            Beacon = new BeaconBlockHeader
            {
                Slot = Number(beacon, "slot"),
                ProposerIndex = Number(beacon, "proposer_index"),
                ParentRoot = Hash(beacon, "parent_root"),
                StateRoot = Hash(beacon, "state_root"),
                BodyRoot = Hash(beacon, "body_root"),
            },
            Execution = new ExecutionPayloadHeader
            {
                ParentHash = Hash(execution, "parent_hash"),
                FeeRecipient = new Address(Hex(execution, "fee_recipient")),
                StateRoot = Hash(execution, "state_root"),
                ReceiptsRoot = Hash(execution, "receipts_root"),
                LogsBloom = new Bloom(Hex(execution, "logs_bloom")),
                PrevRandao = Hash(execution, "prev_randao"),
                BlockNumber = Number(execution, "block_number"),
                GasLimit = Number(execution, "gas_limit"),
                GasUsed = Number(execution, "gas_used"),
                Timestamp = Number(execution, "timestamp"),
                ExtraData = Hex(execution, "extra_data"),
                BaseFeePerGas = new UInt256(Number(execution, "base_fee_per_gas")),
                BlockHash = Hash(execution, "block_hash"),
                TransactionsRoot = Hash(execution, "transactions_root"),
                WithdrawalsRoot = Hash(execution, "withdrawals_root"),
                BlobGasUsed = Number(execution, "blob_gas_used"),
                ExcessBlobGas = Number(execution, "excess_blob_gas"),
            },
            ExecutionBranch = Branch(json, "execution_branch"),
        };
    }

    private static SyncCommittee Committee(JsonElement json) => new()
    {
        Pubkeys = [.. json.GetProperty("pubkeys").EnumerateArray().Select(key => new BlsPublicKey(Convert.FromHexString(key.GetString()![2..])))],
        AggregatePubkey = new BlsPublicKey(Hex(json, "aggregate_pubkey")),
    };

    private static Hash256[] Branch(JsonElement json, string name) =>
        [.. json.GetProperty(name).EnumerateArray().Select(node => new Hash256(node.GetString()!))];

    private static Hash256 Hash(JsonElement json, string name) => new(json.GetProperty(name).GetString()!);
    private static byte[] Hex(JsonElement json, string name) => Convert.FromHexString(json.GetProperty(name).GetString()![2..]);
    private static ulong Number(JsonElement json, string name) => ulong.Parse(json.GetProperty(name).GetString()!);
}
