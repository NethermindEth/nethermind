// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using ValidationResult = Nethermind.Merge.Plugin.Data.ValidationResult;

namespace Nethermind.Merge.Plugin.Test;

public class ProofInclusionListPayloadTests
{
    [Test]
    public void Version_errors_precede_inclusion_proof_shape_errors()
    {
        PayloadAttributes attributes = new()
        {
            Timestamp = 1,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = TestItem.AddressA,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Zero,
            SlotNumber = 1,
            TargetGasLimit = 30_000_000,
            InclusionListTransactions = []
        };
        TestSpecProvider provider = new(Bogota.Instance);
        PayloadAttributesValidationResult expected = attributes.Validate(provider, 4, out string? expectedError);
        attributes.InclusionListRecursiveStark = new([], Keccak.Zero);
        Assert.That(attributes.Validate(provider, 4, out string? actualError), Is.EqualTo(expected));
        Assert.That(actualError, Is.EqualTo(expectedError));

        ExecutionPayloadV4 payload = new()
        {
            Withdrawals = [],
            BlobGasUsed = 0,
            ExcessBlobGas = 0,
            SlotNumber = 1,
            BlockAccessList = [],
            InclusionListRecursiveStark = new([], Keccak.Zero)
        };
        ExecutionPayloadParams<ExecutionPayloadV4> parameters = new(payload, [], Keccak.Zero, [], []);
        Assert.That(parameters.ValidateParams(Bogota.Instance, 4, out string? error), Is.EqualTo(ValidationResult.Fail));
        Assert.That(error, Does.StartWith("Slot number"));
    }

    [Test]
    public void Compliance_digest_separates_transactions_from_proof_metadata()
    {
        byte[] proofBytes = [1];
        Hash256 proofHash = new(ValueKeccak.Compute(proofBytes));
        Block ordinary = Build.A.Block.TestObject;
        ordinary.InclusionListTransactions = [new() { Hash = TestItem.KeccakA }, new() { Hash = TestItem.KeccakB }, new() { Hash = proofHash }];
        Block proven = Build.A.Block.TestObject;
        proven.InclusionListTransactions = [new() { Hash = TestItem.KeccakA }];
        proven.InclusionListRecursiveStark = new(proofBytes, TestItem.KeccakB);
        ValueHash256 digest = NewPayloadHandler.ComputeInclusionListDigest(proven);
        Assert.That(digest, Is.Not.EqualTo(NewPayloadHandler.ComputeInclusionListDigest(ordinary)));
        proofBytes[0] = 2;
        Assert.That(NewPayloadHandler.ComputeInclusionListDigest(proven), Is.Not.EqualTo(digest));
    }

    [TestCase("missing-list")]
    [TestCase("missing-hash")]
    [TestCase("empty-proof")]
    [TestCase("oversized-proof")]
    public void Invalid_sidecar_shape_is_rejected_at_each_payload_entry_point(string invalidShape)
    {
        byte[][]? transactions = invalidShape == "missing-list" ? null : [];
        RecursiveStark proof = new(invalidShape switch
        {
            "empty-proof" => [],
            "oversized-proof" => new byte[Eip8288Constants.MaxProofBytes + 1],
            _ => [1]
        }, invalidShape == "missing-hash" ? null! : Keccak.Zero);
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance) { IsEip7805Enabled = true };
        PayloadAttributes attributes = new() { InclusionListTransactions = transactions, InclusionListRecursiveStark = proof };
        Assert.That(attributes.Validate(new TestSpecProvider(spec), 5, out _),
            Is.EqualTo(PayloadAttributesValidationResult.InvalidPayloadAttributes));

        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(Build.A.Block.TestObject);
        payload.InclusionListTransactions = transactions;
        payload.InclusionListRecursiveStark = proof;
        ExecutionPayloadParams<ExecutionPayloadV3> parameters = new(payload, [], Keccak.Zero, [], transactions);
        Assert.That(parameters.ValidateParams(spec, 6, out _), Is.EqualTo(ValidationResult.Fail));
        Assert.That(payload.TryGetBlock().IsError, Is.True);
    }

    [Test]
    public void Inclusion_proof_is_input_metadata_and_is_not_echoed_by_get_payload()
    {
        RecursiveStark proof = new([1], Keccak.Zero);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        block.InclusionListTransactions = [];
        block.InclusionListRecursiveStark = proof;
        block = block.WithReplacedHeader(block.Header.Clone());
        Assert.That(block.InclusionListRecursiveStark, Is.SameAs(proof));
        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(block);
        Assert.That(payload.InclusionListRecursiveStark, Is.Null);
        // The CL supplies its own list proof on newPayload.
        payload.InclusionListRecursiveStark = proof;
        Result<Block> result = payload.TryGetBlock();
        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.Data!.InclusionListRecursiveStark, Is.SameAs(proof));
    }

    [Test]
    public void Payload_id_commits_to_inclusion_proof()
    {
        BlockHeader parent = Build.A.BlockHeader.TestObject;
        PayloadAttributes original = new() { InclusionListTransactions = [], InclusionListRecursiveStark = new([1], Keccak.Zero) };
        PayloadAttributes replacedProof = new() { InclusionListTransactions = [], InclusionListRecursiveStark = new([2], Keccak.Zero) };
        PayloadAttributes replacedCommitment = new() { InclusionListTransactions = [], InclusionListRecursiveStark = new([1], Keccak.Compute("deps")) };
        Assert.That(original.GetPayloadId(parent), Is.Not.EqualTo(replacedProof.GetPayloadId(parent)));
        Assert.That(original.GetPayloadId(parent), Is.Not.EqualTo(replacedCommitment.GetPayloadId(parent)));
    }
}
