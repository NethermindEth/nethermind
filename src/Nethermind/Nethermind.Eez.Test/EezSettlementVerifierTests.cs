// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Eez.Attestation;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSettlementVerifierTests
{
    private const string Window84 = "captured-devnet-window-84";

    /// <summary>Anvil account 1, the recorded attester; a public test key.</summary>
    private static readonly PrivateKey AttesterKey = new("59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d");

    private static readonly Lazy<EezStatelessBlockResult[]> Window = new(Execute);

    [Test]
    public void Verify_RecordedWindow_ReturnsAndSignsThePublicInputsHashTheAttesterSigned()
    {
        JsonElement oracle = Oracle();

        ValueHash256 publicInputsHash = EezSettlementVerifier.Verify(RecordedBatch(), Window.Value, Context());
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
    public void Verify_TamperedBatch_Throws(Func<PostBatch, PostBatch> tamper) =>
        Assert.Throws<EezSettlementException>(() =>
            EezSettlementVerifier.Verify(EezCalldata.EncodePostAndVerifyBatch(tamper(EezCalldata.DecodePostAndVerifyBatch(RecordedBatch()))), Window.Value, Context()));

    [Test]
    public void Verify_WindowMissingItsFirstBlock_Throws() =>
        Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch(), Window.Value[1..], Context()),
            "the batch starts at the parent of the window's first block");

    [Test]
    public void Verify_MalformedCalldata_Throws() =>
        Assert.Throws<EezSettlementException>(() => EezSettlementVerifier.Verify(RecordedBatch()[..^1], Window.Value, Context()));

    private static TestCaseData[] TamperedBatches() =>
    [
        Case(static b => b with { CallData = DaWithBeneficiary(b.CallData, 0xff) }, "DaClaimsAnotherBeneficiary"),
        Case(static b => b with { CallData = [.. b.CallData, 0x03] }, "DaWithTrailingBytes"),
        Case(static b => b with { BlockNumber = 1 }, "OutsideTheProfile"),
        Case(static b => b with { Entries = [b.Entries[0] with { StateUpdates = [b.Entries[0].StateUpdates[0] with { NewState = default }] }] }, "ClaimsAnotherEnd"),
    ];

    private static byte[] DaWithBeneficiary(byte[] payload, byte value)
    {
        DaPayload decoded = DaPayloadCodec.Decode(payload);
        DaSpan span = decoded.Span;
        (Address, byte[], System.Collections.Generic.IReadOnlyList<byte[]>)[] blocks = Enumerable.Range(0, span.BlockCount)
            .Select(i => (new Address(Enumerable.Repeat(value, Address.Size).ToArray()), span.ExtraData[i].ToArray(),
                (System.Collections.Generic.IReadOnlyList<byte[]>)Array.Empty<byte[]>()))
            .ToArray();
        return DaPayloadCodec.Encode(decoded.RollupId, blocks, decoded.Actions);
    }

    private static TestCaseData Case(Func<PostBatch, PostBatch> tamper, string name) => new(tamper) { TestName = name };

    private static EezSettlementContext Context()
    {
        JsonElement oracle = Oracle();
        return new EezSettlementContext(oracle.GetProperty("rollup_id").GetUInt64(), oracle.GetProperty("l2_chain_id").GetUInt64(),
            new Address(oracle.GetProperty("proof_system").GetString()!), new ValueHash256(oracle.GetProperty("proof_system_vkey").GetString()!));
    }

    private static EezStatelessBlockResult[] Execute() =>
        new EezStatelessExecutor(StatelessFixtures.ReadSpecProvider(Window84, "chain-config.json"), LimboLogs.Instance).Execute(
            Enumerable.Range(79, 6).Select(static n => StatelessFixtures.ReadBlock(Window84, $"block-{n}.rlp.hex", $"witness-{n}.json")).ToArray(), []);

    private static JsonElement Oracle() => StatelessFixtures.ReadJson(Window84, "oracle.json");

    private static byte[] RecordedBatch() =>
        Bytes.FromHexString(File.ReadAllText(StatelessFixtures.PathOf(Window84, "postbatch.hex")).Trim());
}
