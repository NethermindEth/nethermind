// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSettlementVerifierTests
{
    private const string Window84 = "captured-devnet-window-84";

    /// <summary>The recorded attester's key, a well-known public test key.</summary>
    private static readonly PrivateKey AttesterKey = new("59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d");

    private const ulong BlockTimeSeconds = 2;

    private static readonly Lazy<ISpecProvider> Spec = new(static () => StatelessFixtures.ReadSpecProvider(Window84, "chain-config.json"));
    private static readonly Lazy<EezStatelessBlockResult[]> Window = new(Execute);

    [Test]
    public void Verify_RecordedWindow_ReturnsAndSignsThePublicInputsHashTheAttesterSigned()
    {
        JsonElement oracle = Oracle();

        ValueHash256 publicInputsHash = EezSettlementVerifier.Verify(RecordedBatch(), Window.Value, Context(), Spec.Value);
        byte[] signature = new EezAttestationSigner(AttesterKey).Sign(publicInputsHash);

        Assert.That(publicInputsHash, Is.EqualTo(new ValueHash256(oracle.GetProperty("public_inputs_hash").GetString()!)),
            "every check passes on the recorded window and the public inputs hash is the one signed on the devnet");
        Assert.That(signature.ToHexString(true), Is.EqualTo(oracle.GetProperty("expected_test_signature").GetString()),
            "the attestation is the recorded attester's signature, byte for byte");
    }

    [Test]
    public void Signer_Address_IsTheRecordedAttester() =>
        Assert.That(new EezAttestationSigner(AttesterKey).Address, Is.EqualTo(new Address(Oracle().GetProperty("attester").GetString()!)));

    [Test]
    public void Signer_Signature_HasLowSAndRecoverableV()
    {
        EezAttestationSigner signer = new(AttesterKey);
        byte[] halfOrder = Bytes.FromHexString("7fffffffffffffffffffffffffffffff5d576e7357a4501ddfe92f46681b20a0");
        for (int i = 0; i < 64; i++)
        {
            ValueHash256 digest = Keccak.Compute([(byte)i]).ValueHash256;
            byte[] signature = signer.Sign(digest);
            Signature recoverable = new(signature.AsSpan(0, 64), signature[64] - 27);

            Assert.That(signature[64], Is.AnyOf(27, 28), "the verifier accepts only v of 27 or 28");
            Assert.That(signature.AsSpan(32, 32).SequenceCompareTo(halfOrder), Is.LessThanOrEqualTo(0), "the verifier rejects a high s");
            Assert.That(new EthereumEcdsa(0).RecoverAddress(recoverable, in digest), Is.EqualTo(signer.Address),
                "the raw digest is signed, without a message prefix");
        }
    }

    [TestCaseSource(nameof(TamperedBatches))]
    public void Verify_TamperedBatch_Throws(Func<PostBatch, PostBatch> tamper, string rule) =>
        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EezSettlementVerifier.Verify(EezCalldata.EncodePostAndVerifyBatch(tamper(EezCalldata.DecodePostAndVerifyBatch(RecordedBatch()))), Window.Value, Context(), Spec.Value))!.Message,
            Does.Contain(rule));

    [Test]
    public void Verify_EmptyWindow_IsTheCallersFault() =>
        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch(), [], Context(), Spec.Value))!.Failure,
            Is.EqualTo(EezSettlementFailure.InternalInvariant));

    [TestCaseSource(nameof(UnderivableHeaders))]
    public void Verify_HeaderDerivationCannotRebuild_Throws(Action<BlockHeader> tamper, string rule)
    {
        EezStatelessBlockResult[] window = [.. Window.Value];
        BlockHeader header = window[^1].Block.Header.Clone();
        tamper(header);
        window[^1] = window[^1] with { Block = new Block(header, window[^1].Block.Body) };

        EezSettlementException e = Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch(), window, Context(), Spec.Value))!;

        Assert.That(e.Failure, Is.EqualTo(EezSettlementFailure.Rejected));
        Assert.That(e.Message, Does.Contain(rule));
    }

    [Test]
    public void Verify_OtherBlockTime_Throws() =>
        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EezSettlementVerifier.Verify(RecordedBatch(), Window.Value, Context() with { BlockTimeSeconds = BlockTimeSeconds + 1 }, Spec.Value))!.Message,
            Does.Contain("timestamp"));

    [Test]
    public void Verify_OtherGasLimit_Throws() =>
        Assert.That(Assert.Throws<EezSettlementException>(() =>
            EezSettlementVerifier.Verify(RecordedBatch(), Window.Value, Context() with { GasLimit = EezSettlementContext.DefaultGasLimit - 1 }, Spec.Value))!.Message,
            Does.Contain("gas limit"));

    [Test]
    public void Verify_WithdrawalsWhereDerivationHasNone_Throws()
    {
        EezStatelessBlockResult[] window = [.. Window.Value];
        Block settling = window[^1].Block;
        window[^1] = window[^1] with { Block = new Block(settling.Header, settling.Transactions, settling.Uncles, null) };

        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch(), window, Context(), Spec.Value))!.Message,
            Does.Contain("withdrawals"));
    }

    private static TestCaseData[] UnderivableHeaders() =>
    [
        Header(static h => h.Timestamp += 1, "timestamp", "LaterTimestamp"),
        Header(static h => h.GasLimit -= 1, "gas limit", "OtherGasLimit"),
        Header(static h => h.MixHash = Keccak.OfAnEmptyString, "prevRandao", "ChosenPrevRandao"),
        Header(static h => h.ParentBeaconBlockRoot = Keccak.OfAnEmptyString, "beacon block root", "ChosenBeaconRoot"),
        Header(static h => h.ParentBeaconBlockRoot = null, "beacon block root", "NoBeaconRoot"),
        Header(static h => h.Difficulty = 1, "proof-of-work", "Difficulty"),
        Header(static h => h.Nonce = 1, "proof-of-work", "Nonce"),
        Header(static h => h.UnclesHash = Keccak.Zero, "proof-of-work", "UnclesHash"),
    ];

    private static TestCaseData Header(Action<BlockHeader> tamper, string rule, string name) => new(tamper, rule) { TestName = name };

    [Test]
    public void Verify_ContextForL1_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EezSettlementVerifier.Verify(RecordedBatch(), Window.Value, Context() with { RollupId = 0 }, Spec.Value));

    [Test]
    public void Verify_EffectBeforeTheSettlingBlock_Throws()
    {
        SyncSettlementFixture fixture = new();
        EezStatelessBlockResult[] window =
        [
            new(fixture.Settling, fixture.PrecedingBlock.Header, fixture.SettlingReceipts(), []),
            new(fixture.PrecedingBlock, Build.A.BlockHeader.TestObject, [new TxReceipt { StatusCode = 1, Logs = [] }], []),
        ];
        PostBatch recorded = EezCalldata.DecodePostAndVerifyBatch(RecordedBatch());
        StateUpdate chain = new(1, fixture.Settling.ParentHash!.ValueHash256, fixture.PrecedingBlock.Hash!.ValueHash256, Int256.Int256.Zero);
        PostBatch batch = recorded with { Entries = [recorded.Entries[0] with { StateUpdates = [chain] }] };

        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(EezCalldata.EncodePostAndVerifyBatch(batch), window, Context(), Spec.Value))!.Message,
            Does.Contain("only the settling block"));
    }

    [Test]
    public void Verify_WindowMissingItsFirstBlock_Throws() =>
        Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch(), Window.Value[1..], Context(), Spec.Value),
            "the batch starts at the parent of the window's first block");

    [Test]
    public void Verify_MalformedCalldata_IsInvalidPostBatch() =>
        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch()[..^1], Window.Value, Context(), Spec.Value))!.Failure,
            Is.EqualTo(EezSettlementFailure.InvalidPostBatch));

    [Test]
    public void Verify_MalformedDaPayload_IsInvalidDaPayload()
    {
        PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(RecordedBatch());
        byte[] calldata = EezCalldata.EncodePostAndVerifyBatch(batch with { CallData = [.. batch.CallData, 0x03] });

        Assert.That(Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(calldata, Window.Value, Context(), Spec.Value))!.Failure,
            Is.EqualTo(EezSettlementFailure.InvalidDaPayload));
    }

    private static TestCaseData[] TamperedBatches() =>
    [
        Case(static b => b with { CallData = DaWithBeneficiary(b.CallData, 0xff) }, "another beneficiary", "DaClaimsAnotherBeneficiary"),
        Case(static b => b with { CallData = [.. b.CallData, 0x03] }, "truncated", "DaWithTrailingBytes"),
        Case(static b => b with { BlockNumber = 1 }, "block number", "OutsideTheProfile"),
        Case(static b => b with { Entries = [b.Entries[0] with { StateUpdates = [b.Entries[0].StateUpdates[0] with { NewState = default }] }] }, "window's last block",
            "ClaimsAnotherEnd"),
    ];

    private static byte[] DaWithBeneficiary(byte[] payload, byte value)
    {
        DaPayload decoded = DaPayloadCodec.Decode(payload);
        DaSpan span = decoded.Span;
        Address beneficiary = new(Enumerable.Repeat(value, Address.Size).ToArray());
        DaBlock[] blocks = new DaBlock[span.BlockCount];
        int next = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            byte[][] transactions = span.Transactions[next..(next + span.TransactionCounts[i])].Select(static t => t.ToArray()).ToArray();
            next += span.TransactionCounts[i];
            blocks[i] = new DaBlock(i == 0 ? beneficiary : span.Beneficiaries[i], span.ExtraData[i].ToArray(), transactions);
        }

        return DaPayloadCodec.Encode(decoded.RollupId, blocks, decoded.Actions);
    }

    private static TestCaseData Case(Func<PostBatch, PostBatch> tamper, string rule, string name) => new(tamper, rule) { TestName = name };

    private static EezSettlementContext Context()
    {
        JsonElement oracle = Oracle();
        return new EezSettlementContext(oracle.GetProperty("rollup_id").GetUInt64(), oracle.GetProperty("l2_chain_id").GetUInt64(),
            new Address(oracle.GetProperty("proof_system").GetString()!), new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!),
            BlockTimeSeconds);
    }

    private static EezStatelessBlockResult[] Execute() =>
        new EezStatelessExecutor(Spec.Value, LimboLogs.Instance).Execute(
            Enumerable.Range(79, 6).Select(static n => StatelessFixtures.ReadBlock(Window84, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray(), []);

    private static JsonElement Oracle() => StatelessFixtures.ReadJson(Window84, "oracle.json");

    private static byte[] RecordedBatch() => StatelessFixtures.ReadPostBatch(Window84);
}
