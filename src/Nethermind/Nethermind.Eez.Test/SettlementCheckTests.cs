// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SettlementCheckTests
{
    private const string Window = "captured-devnet-anchor-43722";

    /// <summary>
    /// The devnet settles with two attesters, so the mined batch names two proof systems; each attester is sent the
    /// batch naming only its own, which is what the reference signer signed and what the check is run on here.
    /// </summary>
    [Test]
    public void Run_RecordedAnchorWindow_ReturnsThePublicInputsHashTheReferenceSigned()
    {
        (SettlementCheck check, byte[] calldata, EezStatelessBlock[] blocks, (Hash256, Hash256)[] claims, ulong from) = Setup();

        ValueHash256 publicInputsHash = check.Verify(calldata, check.Execute(calldata, blocks, claims, from));

        Assert.That(publicInputsHash, Is.EqualTo(new ValueHash256(Oracle().GetProperty("public_inputs_hash").GetString()!)),
            "the check a composer runs locally reaches the hash the reference signer signed for the same window");
    }

    [Test]
    public void Execute_ClaimedHashDiffers_IsRejected()
    {
        (SettlementCheck check, byte[] calldata, EezStatelessBlock[] blocks, (Hash256 Hash, Hash256 ParentHash)[] claims, ulong from) = Setup();
        claims[^1] = (Keccak.Compute("another block"), claims[^1].ParentHash);

        EezStatelessException e = Assert.Throws<EezStatelessException>(() => check.Execute(calldata, blocks, claims, from))!;

        Assert.That(e.Failure, Is.EqualTo(EezStatelessFailure.Rejected), "a window that is not the claimed one is the composer's fault");
    }

    [Test]
    public void Verify_BatchSettlingAnotherBlock_IsRejected()
    {
        (SettlementCheck check, byte[] calldata, EezStatelessBlock[] blocks, (Hash256, Hash256)[] claims, ulong from) = Setup();
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(calldata);
        StateUpdate update = batch.Entries[0].StateUpdates[0] with { NewState = Keccak.Compute("not the settling block").ValueHash256 };
        byte[] tampered = EezCalldata.EncodePostAndVerifyBatch(batch with
        {
            Entries = [batch.Entries[0] with { StateUpdates = [update], RollingHash = RollingHash.SeedL1(update, default) }],
        });

        Assert.Throws<EezSettlementException>(() => check.Verify(tampered, check.Execute(tampered, blocks, claims, from)),
            "a batch that settles a block the window does not end at is caught before any attester sees it");
    }

    private static (SettlementCheck, byte[], EezStatelessBlock[], (Hash256 Hash, Hash256 ParentHash)[], ulong) Setup()
    {
        JsonElement oracle = Oracle();
        ulong rollupId = oracle.GetProperty("rollup_id").GetUInt64();
        Address proofSystem = new(oracle.GetProperty("proof_system").GetString()!);
        PostBatch mined = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));
        Assert.That(mined.ProofSystems, Does.Contain(proofSystem), "precondition: the reference signer is one of the batch's proof systems");
        byte[] calldata = EezCalldata.EncodePostAndVerifyBatch(mined with
        {
            ProofSystems = [proofSystem],
            RollupIdsWithProofSystems = [new RollupProofSystems(rollupId, [0])],
            Proofs = [],
        });
        EezSettlementContext context = new(rollupId, oracle.GetProperty("l2_chain_id").GetUInt64(), proofSystem,
            new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!), oracle.GetProperty("l2_block_time_seconds").GetUInt64());
        JsonElement[] entries = [.. StatelessFixtures.ReadJson(Window, "blocks.json").EnumerateArray()];
        EezStatelessBlock[] blocks = entries.Select(static e => e.GetProperty("number").GetUInt64())
            .Select(static n => StatelessFixtures.ReadBlock(Window, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray();
        (Hash256, Hash256)[] claims = entries.Select(static e => (new Hash256(e.GetProperty("hash").GetString()!), new Hash256(e.GetProperty("parent_hash").GetString()!))).ToArray();
        return (new SettlementCheck(StatelessFixtures.ReadSpecProvider(Window, "chain-config.json"), context, LimboLogs.Instance), calldata, blocks, claims,
            entries[0].GetProperty("number").GetUInt64());
    }

    private static JsonElement Oracle() => StatelessFixtures.ReadJson(Window, "oracle.json");
}
