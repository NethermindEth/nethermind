// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class ProofInclusionListPayloadTests
{
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
        Assert.That(result.Data.InclusionListRecursiveStark, Is.SameAs(proof));
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
