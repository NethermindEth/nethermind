// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class AnchorBatchTests
{
    private const string Window = "captured-devnet-anchor-43722";

    /// <summary>
    /// The reference composer settled blocks 43717..43722, the first carrying a pure L2 transfer, with an anchor-only
    /// postBatch. Rebuilt from the same blocks and endpoints, with the mined proofs put back, it is the mined calldata.
    /// </summary>
    [Test]
    public void Build_RecordedAnchorWindow_EncodesTheMinedCalldata()
    {
        byte[] mined = StatelessFixtures.ReadPostBatch(Window);
        PostBatch recorded = EezCalldata.DecodePostAndVerifyBatch(mined);
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        Assert.That(recorded.Entries, Has.Length.EqualTo(1), "precondition: the recorded batch is anchor-only");

        PostBatch built = AnchorBatch.Build(oracle.GetProperty("rollup_id").GetUInt64(), Hash(oracle, "window_pre_block_hash"),
            Hash(oracle, "window_post_block_hash"), Span(), recorded.ProofSystems) with
        { Proofs = recorded.Proofs };

        Assert.That(EezCalldata.EncodePostAndVerifyBatch(built), Is.EqualTo(mined), "the batch the reference composer mined, byte for byte");
    }

    [Test]
    public void Build_TwoProofSystems_IndexesBoth()
    {
        Address[] proofSystems = [new("0x0000000000000000000000000000000000000001"), new("0x0000000000000000000000000000000000000002")];

        PostBatch batch = AnchorBatch.Build(1, Keccak.Compute("settled").ValueHash256, Keccak.Compute("last").ValueHash256, [new DaBlock(Address.Zero, [], [])], proofSystems);

        Assert.That(batch.RollupIdsWithProofSystems.Single().ProofSystemIndexes, Is.EqualTo(new ulong[] { 0, 1 }), "every proof system verifies our rollup");
        Assert.That(batch.Proofs, Is.Empty, "the attesters sign the batch before any proof is in it");
    }

    private static DaBlock[] Span() =>
        StatelessFixtures.ReadJson(Window, "blocks.json").EnumerateArray()
            .Select(static entry => Rlp.Decode<Block>(StatelessFixtures.ReadBlock(Window, $"block-{entry.GetProperty("number").GetUInt64()}.rlp.hex"))!)
            .Select(static block => new DaBlock(block.Beneficiary!, block.Header.ExtraData,
                block.Transactions.Select(static t => TxDecoder.Instance.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes).ToArray()))
            .ToArray();

    private static ValueHash256 Hash(JsonElement oracle, string property) => new(oracle.GetProperty(property).GetString()!);
}
