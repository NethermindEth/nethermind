// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

/// <summary>
/// A window recorded on an EEZ devnet whose settling block carries outbound calls and an inbound delivery, so every
/// effect rule runs against a composer's real output rather than against blocks built by the code under test.
/// </summary>
public class EezSettlementEffectsFixtureTests
{
    private const string Window = "captured-devnet-window-438";
    private const int From = 433;
    private const int To = 438;

    /// <summary>The recorded attester's key, a well-known public test key.</summary>
    private static readonly PrivateKey AttesterKey = new("59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d");

    private static readonly Lazy<ISpecProvider> Spec = new(static () => StatelessFixtures.ReadSpecProvider(Window, "chain-config.json"));
    private static readonly Lazy<EezStatelessBlockResult[]> Executed = new(Execute);

    [Test]
    public void Execute_SettlingBlock_ReproducesEveryBlockHash()
    {
        JsonElement[] blocks = [.. StatelessFixtures.ReadJson(Window, "blocks.json").EnumerateArray()];

        Assert.That(Executed.Value.Select(static r => r.Hash.ToString()), Is.EqualTo(blocks.Select(static b => b.GetProperty("hash").GetString())),
            "the window re-executes to the recorded chain, Sync block included");
        Assert.That(Executed.Value[^1].Block.Transactions.Count(static t => t.SenderAddress == EezConstants.SystemAddress), Is.GreaterThan(0),
            "precondition: the settling block carries system transactions");
    }

    [Test]
    public void Verify_EffectWindow_ReturnsThePublicInputsHashTheReferenceSignerAttested()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));

        ValueHash256 publicInputsHash = EezSettlementVerifier.Verify(StatelessFixtures.ReadPostBatch(Window), Executed.Value, Context(oracle), Spec.Value);

        Assert.That(batch.Entries, Has.Length.GreaterThan(2), "precondition: the batch settles an anchor and several effects");
        Assert.That(publicInputsHash, Is.EqualTo(new ValueHash256(oracle.GetProperty("public_inputs_hash").GetString()!)),
            "every effect, DA and Sync-block rule passes on the composer's batch and the digest is the one the reference signer attested");
        Assert.That(new EezAttestationSigner(AttesterKey).Sign(publicInputsHash).ToHexString(true), Is.EqualTo(oracle.GetProperty("mined_proof").GetString()),
            "the attestation is byte for byte the proof L1 accepted");
    }

    [Test]
    public void Verify_EffectWindowWithATamperedAction_Throws()
    {
        JsonElement oracle = StatelessFixtures.ReadJson(Window, "oracle.json");
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(StatelessFixtures.ReadPostBatch(Window));
        DaPayload da = DaPayloadCodec.Decode(batch.CallData);
        DaAction[] actions = [da.Actions[0] with { ReturnData = [.. da.Actions[0].ReturnData, 0] }, .. da.Actions[1..]];
        byte[] tampered = EezCalldata.EncodePostAndVerifyBatch(batch with { CallData = DaPayloadCodec.Encode(da.RollupId, Blocks(da.Span), actions) });

        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(tampered, Executed.Value, Context(oracle), Spec.Value))!.Message,
            Does.Contain("does not rebuild"));
    }

    private static EezSettlementContext Context(JsonElement oracle) => new(oracle.GetProperty("rollup_id").GetUInt64(), oracle.GetProperty("l2_chain_id").GetUInt64(),
        new Address(oracle.GetProperty("proof_system").GetString()!), new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!),
        oracle.GetProperty("l2_block_time_seconds").GetUInt64());

    private static EezStatelessBlockResult[] Execute()
    {
        EezStatelessBlock[] window = Enumerable.Range(From, To - From + 1)
            .Select(static n => StatelessFixtures.ReadBlock(Window, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray();
        int[] checkpoints = SettlingBlock.CheckpointsOf(Rlp.Decode<Block>(window[^1].Rlp)!);
        return new EezStatelessExecutor(Spec.Value, LimboLogs.Instance).Execute(window, checkpoints);
    }

    private static DaBlock[] Blocks(DaSpan span)
    {
        DaBlock[] blocks = new DaBlock[span.BlockCount];
        int next = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            byte[][] transactions = span.Transactions[next..(next + span.TransactionCounts[i])].Select(static t => t.ToArray()).ToArray();
            next += span.TransactionCounts[i];
            blocks[i] = new DaBlock(span.Beneficiaries[i], span.ExtraData[i].ToArray(), transactions);
        }

        return blocks;
    }
}
