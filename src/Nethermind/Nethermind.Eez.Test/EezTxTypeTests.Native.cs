// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezTxTypeTests
{
    [Test]
    public void CreateDecoder_ChoosesExactProfileType([Values] bool native)
    {
        EezChainRules rules = native ? NativeTxRules() : EezChainRules.Rollup0;
        ITxDecoder decoder = EezTxType.CreateDecoder(rules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.Type, Is.EqualTo(native ? EezConstants.NativeSystemTxType : EezConstants.SystemTxType));
            Assert.That(EezTxType.CreateDecoder().Type, Is.EqualTo(EezConstants.SystemTxType));
        }
    }

    [Test]
    public void CreateValidator_NativeType_IsLocalToRegisteredProfile([Values] bool history)
    {
        Transaction tx = NativeEnvelope(history);
        TxValidator native = NativeTxValidator();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new TxValidator(ChainId).IsWellFormed(tx, Osaka.Instance).AsBool(), Is.False);
            Assert.That(_txValidator.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.False);
            Assert.That(native.IsWellFormed(tx, Osaka.Instance).AsBool(), Is.True);
            Assert.That(native.IsWellFormed(SystemTransaction(), Osaka.Instance).AsBool(), Is.False);
        }
    }

    private static readonly string[] InvalidNativeEnvelopeFields =
        ["sender", "target", "source", "nonce", "signature", "gas-price", "max-fee", "blob-fee", "access", "authorization", "blobs", "chain", "history-value", "history-cap", "intrinsic", "floor"];

    [Test]
    public void CreateValidator_NativeInvalidFields_RejectBeforeBlockExecution([ValueSource(nameof(InvalidNativeEnvelopeFields))] string field)
    {
        Transaction tx = NativeEnvelope(field == "history-value");
        switch (field)
        {
            case "sender": tx.SenderAddress = EezConstants.SystemAddress; break;
            case "target": tx.To = TestItem.AddressA; break;
            case "source": tx.SourceHash = null; break;
            case "nonce": tx.Nonce = 1; break;
            case "signature": tx.Signature = Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject.Signature; break;
            case "gas-price": tx.GasPrice = 1; break;
            case "max-fee": tx.DecodedMaxFeePerGas = 1; break;
            case "blob-fee": tx.MaxFeePerBlobGas = UInt256.Zero; break;
            case "access": tx.AccessList = AccessList.Empty; break;
            case "authorization": tx.AuthorizationList = []; break;
            case "blobs": tx.BlobVersionedHashes = []; break;
            case "chain": tx.ChainId = ChainId + 1; break;
            case "history-value": tx.Value = 1; break;
            case "history-cap": tx.To = EezConstants.L1HistoryAddress; tx.GasLimit = EezConstants.L1HistoryTxGasCap + 1; break;
            case "intrinsic": tx.GasLimit = 20_999; break;
            case "floor":
                byte[] bytes = new byte[10_000];
                bytes.AsSpan().Fill(1);
                tx.Data = bytes;
                tx.GasLimit = 200_000;
                break;
        }
        Assert.That(NativeTxValidator().IsWellFormed(tx, Osaka.Instance).AsBool(), Is.False);
    }

    [Test]
    public void CreateEnvironment_UsesOnlySelectedWireProfile([Values] bool native)
    {
        EezChainRules rules = native ? NativeTxRules() : EezChainRules.Rollup0;
        EezStatelessExecutor executor = new(new TestSpecProvider(Osaka.Instance) { ChainId = ChainId }, rules, LimboLogs.Instance);
        using Witness witness = new()
        {
            State = new ArrayPoolList<byte[]>(0),
            Codes = new ArrayPoolList<byte[]>(0),
            Keys = new ArrayPoolList<byte[]>(0),
            Headers = new ArrayPoolList<byte[]>(0)
        };
        ITxValidator validator = executor.CreateEnvironment(witness).TxValidator!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator.IsWellFormed(NativeEnvelope(false), Osaka.Instance).AsBool(), Is.EqualTo(native));
            Assert.That(validator.IsWellFormed(SystemTransaction(), Osaka.Instance).AsBool(), Is.EqualTo(!native));
        }
    }

    private static TxValidator NativeTxValidator()
    {
        TxValidator validator = new(ChainId);
        validator.RegisterValidator(EezConstants.NativeSystemTxType, EezTxType.CreateValidator(ChainId, NativeTxRules()));
        return validator;
    }

    private static EezChainRules NativeTxRules() => EezChainRules.NativeRollupX(
        new RollupXSchedule(2, 6, new RollupXL1Reference(1, 99, TestItem.KeccakA, 108, TestItem.KeccakB), 120), TestItem.AddressD);

    private static Transaction NativeEnvelope(bool history) => new()
    {
        Type = EezConstants.NativeSystemTxType,
        ChainId = ChainId,
        SenderAddress = Address.SystemUser,
        SourceHash = TestItem.KeccakC,
        To = history ? EezConstants.L1HistoryAddress : EezConstants.RollupXEezl2Address,
        GasLimit = history ? EezConstants.L1HistoryTxGasCap : EezChainRules.RollupXSystemTxGasCap,
    };
}
