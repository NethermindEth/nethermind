// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

namespace Nethermind.Eez.Test;

public partial class EezStatelessExecutorTests
{
    private const string NativeCapture = "rollupx/native-offline-bidirectional";

    [Test]
    public void Execute_NativeUpstreamCapture_ReproducesEverySealedBlockWithoutChangingInputs()
    {
        List<ClientFrame> frames = ReadNativeFrames();
        Begin begin = frames[0].Begin;
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(NativeCapture, "l2-genesis.json")));
        Assert.That(begin.AnchorNumber, Is.Zero, "precondition: the cold capture starts at genesis");
        Assert.That(profile.GenesisHash, Is.EqualTo(new ValueHash256(begin.AnchorHash.Span)), "the allocation-derived genesis equals the independently captured anchor");
        NativeL1HeaderRules headerRules = ReadNativeHeaderRules();
        NativeL1HeaderChain evidence = new();
        TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder(profile.Rules));
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
                if (tx.Type == EezConstants.NativeSystemTxType && tx.To == EezConstants.L1HistoryAddress) imports++;
                else if (!tx.IsEezSystemTransaction()) ordinary++;
            }
            parentHash = input.Hash;
            preState = ByteString.CopyFrom(result.PostStateRoot.Bytes);
            count++;
        }
        Finalize finalize = frames[^1].Finalize;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(200), "all captured blocks must execute");
            Assert.That(finalize.TerminalHash, Is.EqualTo(parentHash));
            Assert.That(imports, Is.GreaterThan(0), "recorded native history imports execute with metered gas");
            Assert.That(ordinary, Is.GreaterThan(0), "the capture also executes signed user transactions");
        }
    }

    [Test]
    public void Execute_NativeCaptureWithoutL1Evidence_RejectsWithoutFallback()
    {
        List<ClientFrame> frames = ReadNativeFrames();
        BlockWitness input = frames[1].Block.Data;
        Assert.That(input.Witness.L1HeadersRlp.Count, Is.GreaterThan(0), "precondition: the original first block supplies its parent history endpoint");
        NativeGenesisSnapshot profile = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(NativeCapture, "l2-genesis.json")));
        TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder(profile.Rules));
        EezStatelessExecutor executor = new(profile, new NativeL1HeaderChain(), LimboLogs.Instance);
        using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
        Assert.Throws<EezSettlementException>(() => executor.Execute([new EezStatelessBlock(input.Rlp.ToByteArray(), witness)], []));
    }

    private static List<ClientFrame> ReadNativeFrames()
    {
        byte[] bytes = File.ReadAllBytes(StatelessFixtures.PathOf(NativeCapture, "request.v2.protobuf"));
        Assert.That(ValueKeccak.Compute(bytes), Is.EqualTo(new ValueHash256("0xbda7e319d5b3b8bd30daea948249a8cff50c01248755be8023f0ee31faac5f83")),
            "the upstream captured request remains byte-for-byte unchanged");
        using MemoryStream stream = new(bytes, writable: false);
        List<ClientFrame> frames = [];
        while (stream.Position < stream.Length) frames.Add(ClientFrame.Parser.ParseDelimitedFrom(stream));
        return frames;
    }

    private static NativeL1HeaderRules ReadNativeHeaderRules()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(NativeCapture, "l1-genesis.json")));
        JsonElement config = document.RootElement.GetProperty("config");
        return new NativeL1HeaderRules(Fork("londonBlock"), Fork("shanghaiTime"), Fork("cancunTime"), Fork("pragueTime"), null);

        ulong? Fork(string name) => config.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetUInt64() : null;
    }
}
