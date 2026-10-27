// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezStatelessExecutorTests
{
    [TestCase(0, 2, 0, TestName = "VerifyNativeSuffix_WholeCapture_PreservesBothOriginalGroups")]
    [TestCase(1, 1, 2, TestName = "VerifyNativeSuffix_ConsumedOutbound_PreservesInboundOrdinal")]
    [TestCase(2, 0, 3, TestName = "VerifyNativeSuffix_ConsumedWholeCapture_AcceptsEmptySuffix")]
    [NonParallelizable]
    public void VerifyNativeSuffix_CapturedGroups_MatchesUnchangedExecution(int skip, int take, int firstTransaction)
    {
        NativeGroupCase input = LoadNativeGroups();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.Execution.Block.Transactions, Has.Length.EqualTo(3), "precondition: the capture has one load/user pair and one inbound delivery");
            Assert.That(input.Execution.Block.Transactions[0].SourceHash!.ValueHash256,
                Is.EqualTo(new ValueHash256("0x2ebbdb9e2f6794358439b114f00b8ece8683f77ff78c27d0f42c21d381ef250a")), "unchanged upstream outbound source oracle");
            Assert.That(input.Execution.Block.Transactions[2].SourceHash!.ValueHash256,
                Is.EqualTo(new ValueHash256("0xe026130ff47803ea657e7b52861f5381a98bc2ba7afbf1e05b5204819b8ba52b")), "unchanged upstream inbound source oracle");
        }
        Assert.DoesNotThrow(() => input.Groups.VerifyNativeSuffix(input.Execution, firstTransaction, new ProducingSlice(skip, take), input.Source, input.Rules));
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_MutatedCapturedTransaction_Rejects([ValueSource(nameof(NativeMutationFields))] string field)
    {
        NativeGroupCase input = LoadNativeGroups();
        Transaction native = input.Execution.Block.Transactions[0];
        switch (field)
        {
            case "source": native.SourceHash = TestItem.KeccakA; break;
            case "gas": native.GasLimit--; break;
            case "value": native.Value = native.Value + UInt256.One; break;
            case "data": native.Data = new byte[] { 0 }; break;
            case "chain": native.ChainId = native.ChainId!.Value + 1; break;
            case "target": native.To = TestItem.AddressA; break;
            case "nonce": native.Nonce = 1; break;
            case "sender": native.SenderAddress = TestItem.AddressA; break;
            case "fee": native.GasPrice = UInt256.One; break;
            case "signature": native.Signature = new Signature(1, 2, 27); break;
            default: input.Execution.Block.Transactions[1].Value += UInt256.One; break;
        }
        Assert.That(() => input.Groups.VerifyNativeSuffix(input.Execution, 0, new ProducingSlice(0, 2), input.Source, input.Rules),
            Throws.TypeOf<EezSettlementException>(), "supplied transaction fields cannot replace independently reconstructed contents");
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_RebasedConsumedGroup_Rejects()
    {
        NativeGroupCase input = LoadNativeGroups();
        input.Execution.Block.Transactions[2].SourceHash = new Hash256(input.Source.Entry(0).Bytes);
        Assert.That(() => input.Groups.VerifyNativeSuffix(input.Execution, 2, new ProducingSlice(1, 1), input.Source, input.Rules),
            Throws.TypeOf<EezSettlementException>(), "an applied suffix retains ordinal one from the complete original stream");
    }

    [TestCaseSource(nameof(NativeInvalidSelections))]
    [NonParallelizable]
    public void VerifyNativeSuffix_InvalidSelection_Rejects(int skip, int take, int firstTransaction)
    {
        NativeGroupCase input = LoadNativeGroups();
        Assert.That(() => input.Groups.VerifyNativeSuffix(input.Execution, firstTransaction, new ProducingSlice(skip, take), input.Source, input.Rules),
            Throws.TypeOf<EezSettlementException>(), "reconstruction must cover complete groups and the exact selected transaction suffix");
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_InconsistentGasEvidence_Rejects([Values("decrease", "limit", "terminal")] string field)
    {
        NativeGroupCase input = LoadNativeGroups();
        TxReceipt[] receipts = input.Execution.Receipts;
        switch (field)
        {
            case "decrease": receipts[1].GasUsedTotal = receipts[0].GasUsedTotal - 1; break;
            case "limit":
                receipts[^1].GasUsedTotal = input.Execution.Block.GasLimit + 1;
                input.Execution.Block.Header.GasUsed = receipts[^1].GasUsedTotal;
                break;
            default: receipts[^1].GasUsedTotal--; break;
        }
        Assert.That(() => input.Groups.VerifyNativeSuffix(input.Execution, 0, new ProducingSlice(0, 2), input.Source, input.Rules),
            Throws.TypeOf<EezSettlementException>(), "position gas must come from complete monotonic execution evidence");
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_WrongReceiptCount_Rejects([Values] bool extra)
    {
        NativeGroupCase input = LoadNativeGroups();
        TxReceipt[] receipts = extra ? [.. input.Execution.Receipts, new TxReceipt()] : input.Execution.Receipts[..^1];
        EezStatelessBlockResult changed = input.Execution with { Receipts = receipts };
        Assert.That(() => input.Groups.VerifyNativeSuffix(changed, 0, new ProducingSlice(0, 2), input.Source, input.Rules),
            Throws.TypeOf<EezSettlementException>(), "the checked gas facts must cover every actual transaction");
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_GasBelowCap_RequiresTheExactExecutedPosition([Values] bool exact)
    {
        NativeGroupCase input = LoadNativeGroups();
        // This arithmetic-only case changes checked gas inputs; it does not claim a new executed block or sealed hash.
        ulong priorGas = input.Execution.Block.GasLimit - EezChainRules.RollupXSystemTxGasCap + 1;
        input.Execution.Receipts[1].GasUsedTotal = priorGas;
        input.Execution.Receipts[2].GasUsedTotal = priorGas + 1;
        input.Execution.Block.Header.GasUsed = priorGas + 1;
        input.Execution.Block.Transactions[2].GasLimit = EezChainRules.RollupXSystemTxGasCap - (exact ? 1UL : 0UL);
        Action verify = () => input.Groups.VerifyNativeSuffix(input.Execution, 2, new ProducingSlice(1, 1), input.Source, input.Rules);
        if (exact) Assert.DoesNotThrow(verify);
        else Assert.That(verify, Throws.TypeOf<EezSettlementException>(), "the global cap cannot replace a smaller remaining block budget");
    }

    [Test]
    [NonParallelizable]
    public void VerifyNativeSuffix_OtherChainOrProfile_Rejects([Values] bool otherChain)
    {
        NativeGroupCase input = LoadNativeGroups();
        NativeLiveSource source = otherChain ? new NativeLiveSource(input.Source.ChainId + 1, TestItem.KeccakA.ValueHash256) : input.Source;
        if (otherChain)
        {
            input.Execution.Block.Transactions[0].SourceHash = new Hash256(source.Entry(0).Bytes);
            input.Execution.Block.Transactions[2].SourceHash = new Hash256(source.Entry(1).Bytes);
        }
        EezChainRules rules = otherChain ? input.Rules
            : EezChainRules.NativeRollupX(input.Rules.Schedule!, input.Rules.FeeCollector!, NativeSystemTxFormat.RecordedV1);
        Assert.That(() => input.Groups.VerifyNativeSuffix(input.Execution, 0, new ProducingSlice(0, 2), source, rules),
            Throws.TypeOf<EezSettlementException>(), "reconstruction is available only for the selected native V3 chain");
    }

    private static IEnumerable<string> NativeMutationFields() =>
        ["source", "gas", "value", "data", "chain", "target", "nonce", "sender", "fee", "signature", "user"];

    private static IEnumerable<TestCaseData> NativeInvalidSelections() =>
    [
        new TestCaseData(-1, 1, 0).SetName("VerifyNativeSuffix_NegativeSkip_Rejects"),
        new TestCaseData(0, -1, 0).SetName("VerifyNativeSuffix_NegativeTake_Rejects"),
        new TestCaseData(3, 0, 3).SetName("VerifyNativeSuffix_SkipPastLastGroup_Rejects"),
        new TestCaseData(1, 2, 2).SetName("VerifyNativeSuffix_TakePastLastGroup_Rejects"),
        new TestCaseData(1, 1, 1).SetName("VerifyNativeSuffix_StartInsideLoadUserPair_Rejects"),
        new TestCaseData(0, 1, 0).SetName("VerifyNativeSuffix_UnconsumedTrailingDelivery_Rejects"),
        new TestCaseData(0, 2, 3).SetName("VerifyNativeSuffix_MissingGroup_Rejects"),
        new TestCaseData(2, 0, 4).SetName("VerifyNativeSuffix_TransactionOffsetPastBlock_Rejects"),
        new TestCaseData(0, 2, -1).SetName("VerifyNativeSuffix_NegativeTransactionOffset_Rejects"),
    ];

    private static NativeGroupCase LoadNativeGroups()
    {
        const string capture = "rollupx/native-offline-v3";
        List<ClientFrame> frames = ReadNativeFrames(capture);
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV3);
        NativeL1HeaderRules headerRules = ReadNativeHeaderRules(capture);
        NativeL1HeaderChain evidence = new();
        BlockWitness terminal = null!;
        foreach (ClientFrame frame in frames)
        {
            if (frame.Block is null) continue;
            terminal = frame.Block.Data;
            ReadOnlyMemory<byte>[] headers = new ReadOnlyMemory<byte>[terminal.Witness.L1HeadersRlp.Count];
            for (int index = 0; index < headers.Length; index++) headers[index] = terminal.Witness.L1HeadersRlp[index].Memory;
            evidence.Append(headers, in headerRules);
        }
        SettlementInput settlement = frames[^1].Finalize.Settlement;
        byte[][] blobs = new byte[settlement.Blobs.Count][];
        for (int index = 0; index < blobs.Length; index++) blobs[index] = settlement.Blobs[index].ToByteArray();
        NativeStream stream = NativeStream.Decode(BlobStream.Unpack(blobs), frames[0].Begin.RollupId);
        EezSettlementContext context = new(frames[0].Begin.RollupId, genesis.Specs.ChainId, Address.Zero, default, 1);
        NativeTransactions groups = NativeTransactions.Parse(stream.Messages, context);
        NativeLiveSource source = new(context.ChainId, new ValueHash256(settlement.PostingContext.ParentHash.Span));
        using (new NativeDecoderScope(genesis.Rules))
        using (Witness witness = WindowAssembler.ToL2Witness(terminal.Witness))
        {
            EezStatelessBlockResult execution = new EezStatelessExecutor(genesis, evidence, LimboLogs.Instance)
                .Execute([new EezStatelessBlock(terminal.Rlp.ToByteArray(), witness)], [])[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(execution.Hash.ValueHash256, Is.EqualTo(new ValueHash256(terminal.Hash.Span)), "precondition: replay matches the original sealed terminal block");
                Assert.That(Rlp.Encode(execution.Block).Bytes, Is.EqualTo(terminal.Rlp.ToByteArray()), "precondition: original consensus bytes remain unchanged");
            }
            return new NativeGroupCase(groups, source, genesis.Rules, execution);
        }
    }

    private sealed record NativeGroupCase(NativeTransactions Groups, NativeLiveSource Source, EezChainRules Rules, EezStatelessBlockResult Execution);
}
