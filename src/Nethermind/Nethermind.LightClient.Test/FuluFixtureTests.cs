// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Text.Json;
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
    public void Captured_mainnet_bootstrap_and_finality_update_authenticate()
    {
        using JsonDocument bootstrapJson = Read("bootstrap");
        using JsonDocument finalityJson = Read("finality");
        Assert.That(bootstrapJson.RootElement.GetProperty("version").GetString(), Is.EqualTo("fulu"));
        Assert.That(finalityJson.RootElement.GetProperty("version").GetString(), Is.EqualTo("fulu"));

        LightClientBootstrap bootstrap = Bootstrap(bootstrapJson.RootElement.GetProperty("data"));
        LightClientFinalityUpdate finality = Finality(finalityJson.RootElement.GetProperty("data"));
        Assert.That(SszRoots.HashTreeRoot(bootstrap.Header!.Beacon!), Is.EqualTo(Checkpoint));

        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        LightClientBootstrap decodedBootstrap = LightClientWireCodec.DecodeBootstrap(LightClientWireCodec.EncodeBootstrap(bootstrap, spec), spec);
        LightClientFinalityUpdate decodedFinality = LightClientWireCodec.DecodeFinality(LightClientWireCodec.EncodeFinality(finality, spec), spec);
        LightClientStore store = new(spec, Checkpoint, decodedBootstrap, decodedFinality.SignatureSlot);
        store.Process(decodedFinality, decodedFinality.SignatureSlot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.FinalizedHeader.Beacon!.Slot, Is.EqualTo(15384736UL));
            Assert.That(store.OptimisticHeader.Beacon!.Slot, Is.EqualTo(15384813UL));
            Assert.That(store.FinalizedHeader.Execution!.BlockHash,
                Is.EqualTo(new Hash256("0xaf43d16d79770a96dbe621e0ae711cabb98319b5fa1a260ea22080947b48fb0a")));
        }

        decodedFinality.FinalityBranch![0] = Hash256.Zero;
        LightClientStore fresh = new(spec, Checkpoint, decodedBootstrap, decodedFinality.SignatureSlot);
        Assert.That(() => fresh.Process(decodedFinality, decodedFinality.SignatureSlot),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("finality proof"));
    }

    private static JsonDocument Read(string name) => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Fulu", name + ".json")));

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
