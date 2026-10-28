// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Google.Protobuf;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test;

public partial class EezStatelessExecutorTests
{
    private const string NativeCapture = "rollupx/native-offline-bidirectional";

    [TestCase(1, 200)]
    [TestCase(3, 30)]
    [TestCase(4, 30)]
    [TestCase(6, 30)]
    [NonParallelizable]
    public void Execute_NativeUpstreamCapture_ReproducesEverySealedBlockWithoutChangingInputs(int version, int expectedBlocks)
    {
        string capture = NativeCaptureFor(version);
        List<ClientFrame> frames = ReadNativeFrames(capture);
        Begin begin = frames[0].Begin;
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")),
            (NativeSystemTxFormat)version);
        Assert.That(begin.AnchorNumber, Is.Zero, "precondition: the cold capture starts at genesis");
        Assert.That(profile.GenesisHash, Is.EqualTo(new ValueHash256(begin.AnchorHash.Span)), "the allocation-derived genesis equals the independently captured anchor");
        NativeL1HeaderRules headerRules = ReadNativeHeaderRules(capture);
        NativeL1HeaderChain evidence = new();
        using (new NativeDecoderScope(profile.Rules))
        {
            EezStatelessExecutor executor = new(profile, evidence, LimboLogs.Instance);
            ByteString parentHash = begin.AnchorHash;
            ByteString preState = begin.AnchorStateRoot;
            int count = 0, imports = 0, ordinary = 0;
            foreach (ClientFrame frame in frames)
            {
                if (frame.KindCase != ClientFrame.KindOneofCase.Block) continue;
                BlockWitness input = frame.Block.Data;
                ReadOnlyMemory<byte>[] segment = new ReadOnlyMemory<byte>[input.Witness.L1HeadersRlp.Count];
                for (int index = 0; index < segment.Length; index++) segment[index] = input.Witness.L1HeadersRlp[index].Memory;
                evidence.Append(segment, in headerRules);
                using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
                EezStatelessBlockResult result = executor.Execute([new EezStatelessBlock(input.Rlp.ToByteArray(), witness)], [])[0];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.Block.Number, Is.EqualTo(input.Number), "recorded block number");
                    Assert.That(result.Hash.ValueHash256, Is.EqualTo(new ValueHash256(input.Hash.Span)), $"block {input.Number}: independently sealed hash");
                    Assert.That(result.Parent.Hash!.ValueHash256, Is.EqualTo(new ValueHash256(parentHash.Span)), $"block {input.Number}: exact predecessor");
                    Assert.That(result.PreStateRoot.ValueHash256, Is.EqualTo(new ValueHash256(preState.Span)), $"block {input.Number}: authenticated pre-state");
                    Assert.That(Rlp.Encode(result.Block).Bytes, Is.EqualTo(input.Rlp.ToByteArray()), $"block {input.Number}: unchanged consensus bytes");
                }
                foreach (Transaction tx in result.Block.Transactions)
                {
                    if (tx.To == EezConstants.L1HistoryAddress && tx.IsEezSystemTransaction()) imports++;
                    else if (!tx.IsEezSystemTransaction()) ordinary++;
                }
                parentHash = input.Hash;
                preState = ByteString.CopyFrom(result.PostStateRoot.Bytes);
                count++;
            }
            Finalize finalize = frames[^1].Finalize;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(count, Is.EqualTo(expectedBlocks), "all captured blocks must execute");
                Assert.That(finalize.TerminalHash, Is.EqualTo(parentHash));
                Assert.That(imports, Is.GreaterThan(0), "recorded native history imports execute with metered gas");
                Assert.That(ordinary, Is.GreaterThan(0), "the capture also executes signed user transactions");
            }
        }
    }

    [Test]
    [NonParallelizable]
    public void Execute_NativeCaptureWithoutL1Evidence_RejectsWithoutFallback([Values(1, 3, 4, 6)] int version)
    {
        string capture = NativeCaptureFor(version);
        List<ClientFrame> frames = ReadNativeFrames(capture);
        BlockWitness input = frames[1].Block.Data;
        Assert.That(input.Witness.L1HeadersRlp.Count, Is.GreaterThan(0), "precondition: the original first block supplies its parent history endpoint");
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")),
            (NativeSystemTxFormat)version);
        using (new NativeDecoderScope(profile.Rules))
        {
            EezStatelessExecutor executor = new(profile, new NativeL1HeaderChain(), LimboLogs.Instance);
            using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
            Assert.Throws<EezSettlementException>(() => executor.Execute([new EezStatelessBlock(input.Rlp.ToByteArray(), witness)], []));
        }
    }

    private static string NativeCaptureFor(int version) => version == 1 ? NativeCapture : $"rollupx/native-offline-v{version}";

    private static List<ClientFrame> ReadNativeFrames(string capture)
    {
        byte[] bytes = File.ReadAllBytes(StatelessFixtures.PathOf(capture, "request.v2.protobuf"));
        Assert.That(ValueKeccak.Compute(bytes), Is.EqualTo(new ValueHash256(capture == NativeCapture
            ? "0xbda7e319d5b3b8bd30daea948249a8cff50c01248755be8023f0ee31faac5f83"
            : capture == "rollupx/native-offline-v6"
                ? "0xb2e42e07bef03c231f66973cd43afad57f3c5720d6ce7eb9e391a0e391dd3278"
                : capture == "rollupx/native-offline-v4"
                ? "0x4fe2bd2df0167990164d7004a44e141768ecd70595db718dade3e18c054ff715"
                : "0xaefdb086b271c4be7f8c09155236994a2f71cd797e9d7232373f415fdae93459")),
            "the upstream captured request remains byte-for-byte unchanged");
        using MemoryStream stream = new(bytes, writable: false);
        List<ClientFrame> frames = [];
        while (stream.Position < stream.Length) frames.Add(ClientFrame.Parser.ParseDelimitedFrom(stream));
        return frames;
    }

    [Test]
    [NonParallelizable]
    public void Execute_ContractV3Checkpoints_RejectsUnpairedLoadAndAcceptsCompleteUserPrefix()
    {
        const string capture = "rollupx/native-offline-v3";
        List<ClientFrame> frames = ReadNativeFrames(capture);
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV3);
        NativeL1HeaderRules headerRules = ReadNativeHeaderRules(capture);
        NativeL1HeaderChain evidence = new();
        using (new NativeDecoderScope(profile.Rules))
        {
            EezStatelessExecutor executor = new(profile, evidence, LimboLogs.Instance);
            foreach (ClientFrame frame in frames)
            {
                if (frame.KindCase != ClientFrame.KindOneofCase.Block) continue;
                BlockWitness input = frame.Block.Data;
                ReadOnlyMemory<byte>[] segment = new ReadOnlyMemory<byte>[input.Witness.L1HeadersRlp.Count];
                for (int index = 0; index < segment.Length; index++) segment[index] = input.Witness.L1HeadersRlp[index].Memory;
                evidence.Append(segment, in headerRules);
                Block decoded = Rlp.Decode<Block>(input.Rlp.ToByteArray())!;
                for (int index = 0; index < decoded.Transactions.Length; index++)
                {
                    Transaction tx = decoded.Transactions[index];
                    if (tx.Type != EezConstants.NativeSystemTxType || tx.Data.Length < 4 ||
                        BinaryPrimitives.ReadUInt32BigEndian(tx.Data.Span) != EezCalldata.LoadExecutionTableSelector) continue;
                    using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
                    EezStatelessBlock[] window = [new(input.Rlp.ToByteArray(), witness)];
                    int loadIndex = index;
                    Assert.That(() => executor.Execute(window, [loadIndex]), Throws.TypeOf<EezSettlementException>(),
                        "a prefix after a load but before its user cannot be a candidate block");
                    Assert.That(index + 1, Is.LessThan(decoded.Transactions.Length));
                    int last = decoded.Transactions.Length - 1;
                    int[] checkpoints = index + 1 == last ? [last] : [index + 1, last];
                    EezStatelessBlockResult result = executor.Execute(window, checkpoints)[0];
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(result.Checkpoints[0].TransactionIndex, Is.EqualTo(index + 1));
                        Assert.That(result.Checkpoints[^1].StateRoot, Is.EqualTo(result.PostStateRoot));
                        Assert.That(result.Checkpoints[^1].BlockHash.ValueHash256, Is.EqualTo(new ValueHash256(input.Hash.Span)),
                            "the complete final prefix equals the independently sealed upstream block");
                    }
                    return;
                }
            }
        }
        Assert.Fail("The unchanged upstream capture must contain an outbound load/user group.");
    }

    [Test]
    [NonParallelizable]
    public void Execute_ContractV3EmptyPrefix_RequiresInheritedRandomness([Values] bool changedOrigin)
    {
        const string capture = "rollupx/native-offline-v3";
        List<ClientFrame> frames = ReadNativeFrames(capture);
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV3);
        NativeL1HeaderRules headerRules = ReadNativeHeaderRules(capture);
        NativeL1HeaderChain evidence = new();
        ValueHash256 previousMix = profile.Rules.Schedule!.L1Reference.PrevRandao.ValueHash256;
        using (new NativeDecoderScope(profile.Rules))
        {
            EezStatelessExecutor executor = new(profile, evidence, LimboLogs.Instance);
            foreach (ClientFrame frame in frames)
            {
                if (frame.KindCase != ClientFrame.KindOneofCase.Block) continue;
                BlockWitness input = frame.Block.Data;
                ReadOnlyMemory<byte>[] segment = new ReadOnlyMemory<byte>[input.Witness.L1HeadersRlp.Count];
                for (int index = 0; index < segment.Length; index++) segment[index] = input.Witness.L1HeadersRlp[index].Memory;
                evidence.Append(segment, in headerRules);
                Block block = Rlp.Decode<Block>(input.Rlp.ToByteArray())!;
                bool changed = block.Header.MixHash!.ValueHash256 != previousMix;
                previousMix = block.Header.MixHash.ValueHash256;
                if (changed != changedOrigin) continue;
                using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
                EezStatelessBlock[] window = [new(input.Rlp.ToByteArray(), witness)];
                if (changed)
                {
                    Assert.That((byte)block.Transactions[0].Type, Is.EqualTo(NativeHistoryEnvelope.Type));
                    Assert.That(() => executor.Execute(window, [EezTransactionCheckpoint.PreExecution]),
                        Throws.TypeOf<EezSettlementException>().With.Message.Contains("empty prefix PREVRANDAO"));
                }
                else
                {
                    EezStatelessBlockResult result = executor.Execute(window, [EezTransactionCheckpoint.PreExecution])[0];
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(result.Checkpoints[0].TransactionIndex, Is.EqualTo(EezTransactionCheckpoint.PreExecution));
                        Assert.That(result.Hash.ValueHash256, Is.EqualTo(new ValueHash256(input.Hash.Span)));
                    }
                }
                return;
            }
        }
        Assert.Fail("The unchanged upstream capture must cover both inherited and newly selected randomness.");
    }

    private sealed class NativeDecoderScope : IDisposable
    {
        internal NativeDecoderScope(EezChainRules rules)
        {
            TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder(rules));
            if (rules.UsesNativeContract) TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(rules.NativeFormat));
        }

        public void Dispose()
        {
            TxDecoder.Instance.RegisterDecoder(new EezNativeSystemTxDecoder());
            TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder());
        }
    }

    private static NativeL1HeaderRules ReadNativeHeaderRules(string capture)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l1-genesis.json")));
        JsonElement config = document.RootElement.GetProperty("config");
        return new NativeL1HeaderRules(Fork("londonBlock"), Fork("shanghaiTime"), Fork("cancunTime"), Fork("pragueTime"), null);

        ulong? Fork(string name) => config.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetUInt64() : null;
    }
}
