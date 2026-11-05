// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using Block = Nethermind.Core.Block;

namespace Nethermind.Eez.Test;

public partial class EezStatelessExecutorTests
{
    [Test]
    [NonParallelizable]
    public void Execute_UpstreamHistoryDonationFixture_ClosedCheckpointsMatchUntouchedSealedBlocks([Values("static", "lookup")] string kind)
    {
        const string capture = "rollupx/native-history-observations-v6";
        string request = kind + "-correct.v2.protobuf";
        byte[] bytes = File.ReadAllBytes(StatelessFixtures.PathOf(capture, request));
        JsonElement identity = StatelessFixtures.ReadJson(capture, "identity.json");
        foreach (JsonElement expected in identity.GetProperty("cases").EnumerateArray())
            if (expected.GetProperty("request").GetString() == request)
                Assert.That(ValueKeccak.Compute(bytes), Is.EqualTo(new ValueHash256(expected.GetProperty("request_hash").GetString()!)));
        List<ClientFrame> frames = [];
        using (MemoryStream stream = new(bytes, writable: false))
            while (stream.Position < stream.Length) frames.Add(ClientFrame.Parser.ParseDelimitedFrom(stream));
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV6);
        NativeL1HeaderRules headers = ReadNativeHeaderRules(capture);
        NativeL1HeaderChain evidence = new();
        int blocks = 0;
        using (new NativeDecoderScope(genesis.Rules))
        {
            EezStatelessExecutor executor = new(genesis, evidence, LimboLogs.Instance);
            foreach (ClientFrame frame in frames)
            {
                if (frame.Block is null) continue;
                BlockWitness input = frame.Block.Data;
                ReadOnlyMemory<byte>[] segment = new ReadOnlyMemory<byte>[input.Witness.L1HeadersRlp.Count];
                for (int index = 0; index < segment.Length; index++) segment[index] = input.Witness.L1HeadersRlp[index].Memory;
                evidence.Append(segment, headers);
                Block decoded = Rlp.Decode<Block>(input.Rlp.ToByteArray())!;
                bool terminal = input.Number == frames[^1].Finalize.ToBlock;
                if (terminal)
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(decoded.Transactions, Has.Length.EqualTo(2));
                        Assert.That(decoded.Transactions[1].To, Is.EqualTo(Address.SystemUser));
                        Assert.That(decoded.Transactions[1].ValueRef, Is.EqualTo((UInt256)7));
                    }
                }
                using Witness witness = WindowAssembler.ToL2Witness(input.Witness);
                EezStatelessBlockResult result = executor.Execute([new EezStatelessBlock(input.Rlp.ToByteArray(), witness)], terminal ? [0, 1] : [])[0];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.Hash.ValueHash256, Is.EqualTo(new ValueHash256(input.Hash.Span)));
                    Assert.That(Rlp.Encode(result.Block).Bytes, Is.EqualTo(input.Rlp.ToByteArray()));
                    if (terminal)
                    {
                        Assert.That(result.Checkpoints[^1].StateRoot, Is.EqualTo(result.PostStateRoot));
                        Assert.That(result.Checkpoints[^1].BlockHash.ValueHash256, Is.EqualTo(new ValueHash256(input.Hash.Span)),
                            "the independently supplied closed block burns the donation without rewriting the captured request");
                    }
                }
                blocks++;
            }
        }
        Assert.That(blocks, Is.EqualTo(6), "the complete supplied execution fixture must replay");
    }
}
