// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.LightClient.Test;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;

namespace Nethermind.LightClient.Verifier.Test;

[TestFixture]
public class BrowserSmokeFixtureTests
{
    [Test, Explicit("Exports signed consensus fixtures for the Chrome smoke runner.")]
    public async Task Export_browser_fixture()
    {
        string directory = Environment.GetEnvironmentVariable("NETHERMIND_BROWSER_TEST_FIXTURES")
            ?? throw new InvalidOperationException("Set NETHERMIND_BROWSER_TEST_FIXTURES to the prepared temporary fixture directory.");
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("The browser fixture directory must be absolute.");
        string proofPath = Path.Combine(directory, "synthetic-proof.json");
        JsonObject proof = JsonNode.Parse(await File.ReadAllTextAsync(proofPath))!.AsObject();
        BeaconChainSpec spec = BeaconChainSpec.Mainnet;
        ulong slot = spec.GetSlotAtTime((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 20;
        LightClientBootstrap bootstrap = ConsensusTests.Bootstrap(slot, 1);
        ExecutionPayloadHeader payload = bootstrap.Header!.Execution!;
        payload.StateRoot = new Hash256(proof["stateRoot"]!.GetValue<string>());
        payload.BlockNumber = 26_100_000;
        payload.GasLimit = 30_000_000;
        payload.GasUsed = 0;
        payload.Timestamp = spec.GenesisTime + slot * spec.SecondsPerSlot;
        payload.TransactionsRoot = Keccak.EmptyTreeHash;
        payload.ReceiptsRoot = Keccak.EmptyTreeHash;
        payload.WithdrawalsRoot = Hash256.Zero;
        BlockHeader header = new(payload.ParentHash!, Keccak.OfAnEmptySequenceRlp, payload.FeeRecipient!,
            0, payload.BlockNumber, payload.GasLimit, payload.Timestamp, payload.ExtraData ?? [])
        {
            StateRoot = payload.StateRoot,
            TxRoot = payload.TransactionsRoot,
            ReceiptsRoot = payload.ReceiptsRoot,
            Bloom = payload.LogsBloom!,
            BaseFeePerGas = payload.BaseFeePerGas,
            MixHash = payload.PrevRandao,
            BlobGasUsed = payload.BlobGasUsed,
            ExcessBlobGas = payload.ExcessBlobGas,
            WithdrawalsRoot = payload.WithdrawalsRoot,
            IsPostMerge = true,
        };
        byte[] encodedHeader = new HeaderDecoder(MainnetSpecProvider.Instance).Encode(header).Bytes;
        payload.BlockHash = Keccak.Compute(encodedHeader);
        Hash256[] body = ConsensusTests.Tree(new() { [25] = SszRoots.HashTreeRoot(payload) });
        bootstrap.Header.Beacon!.BodyRoot = body[1];
        bootstrap.Header.ExecutionBranch = ConsensusTests.Branch(body, 25);
        Hash256 checkpoint = SszRoots.HashTreeRoot(bootstrap.Header.Beacon);
        LightClientUpdate update = ConsensusTests.Update(slot, slot + 10, slot + 11, 1, nextKey: 1);
        foreach (LightClientHeader selected in new[] { update.AttestedHeader!, update.FinalizedHeader! })
        {
            selected.Execution = payload;
            selected.Beacon!.BodyRoot = body[1];
            selected.ExecutionBranch = bootstrap.Header.ExecutionBranch;
        }
        Hash256[] state = ConsensusTests.Tree(new()
        {
            [169] = SszRoots.HashTreeRoot(update.FinalizedHeader!.Beacon!),
            [87] = SszRoots.HashTreeRoot(update.NextSyncCommittee!),
        });
        update.AttestedHeader!.Beacon!.StateRoot = state[1];
        update.FinalityBranch = ConsensusTests.Branch(state, 169);
        update.NextSyncCommitteeBranch = ConsensusTests.Branch(state, 87);
        ConsensusTests.Sign(update, 1, 512, spec);
        LightClientFinalityUpdate finality = new()
        {
            AttestedHeader = update.AttestedHeader,
            FinalizedHeader = update.FinalizedHeader,
            FinalityBranch = update.FinalityBranch,
            SyncAggregate = update.SyncAggregate,
            SignatureSlot = update.SignatureSlot,
        };
        LightClientOptimisticUpdate optimistic = new()
        {
            AttestedHeader = update.AttestedHeader,
            SyncAggregate = update.SyncAggregate,
            SignatureSlot = update.SignatureSlot,
        };
        byte[][] nodes = proof["accountProof"]!.AsArray().Select(node => Convert.FromHexString(node!.GetValue<string>()[2..])).ToArray();
        Account account = ExecutionProofVerifier.VerifyAccount(payload.StateRoot, new Address(proof["address"]!.GetValue<string>()), nodes);
        Assert.That(account.Balance, Is.EqualTo((Nethermind.Int256.UInt256)123));
        await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-bootstrap.ssz"), LightClientWireCodec.EncodeBootstrap(bootstrap, spec));
        await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-update.ssz"), LightClientWireCodec.EncodeUpdate(update, spec));
        await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-finality.ssz"), LightClientWireCodec.EncodeFinality(finality, spec));
        await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-optimistic.ssz"), LightClientWireCodec.EncodeOptimistic(optimistic, spec));
        await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-header.rlp"), encodedHeader);
        proof["checkpoint"] = checkpoint.ToString();
        proof["blockHash"] = payload.BlockHash.ToString();
        await File.WriteAllTextAsync(proofPath, proof.ToJsonString());
    }
}
