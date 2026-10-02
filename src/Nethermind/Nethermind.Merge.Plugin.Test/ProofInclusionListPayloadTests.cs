// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using ValidationResult = Nethermind.Merge.Plugin.Data.ValidationResult;

namespace Nethermind.Merge.Plugin.Test;

public class ProofInclusionListPayloadTests
{
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
    public void Sidecar_survives_payload_and_block_header_replacement()
    {
        RecursiveStark proof = new([1], Keccak.Zero);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        block.InclusionListTransactions = [];
        block.InclusionListRecursiveStark = proof;
        block = block.WithReplacedHeader(block.Header.Clone());
        ExecutionPayloadV3 payload = ExecutionPayloadV3.Create(block);
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
