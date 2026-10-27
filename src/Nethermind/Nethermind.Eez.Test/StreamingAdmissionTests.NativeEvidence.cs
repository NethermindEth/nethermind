// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Google.Protobuf;
using Grpc.Core;
using Nethermind.Consensus.Stateless;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove;
using Nethermind.Eez.Prove.V2;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class StreamingAdmissionTests
{
    private static readonly string[] CurrentStreamingProfiles = Enum.GetNames<StreamingSettlementProfile>();

    [Test]
    public void ToWitness_UnsupportedL1Evidence_RejectsInsteadOfDroppingIt()
    {
        ExecutionWitness witness = new() { L1HeadersRlp = { ByteString.CopyFrom([0xc0]) } };
        Assert.That(Assert.Throws<RpcException>(() =>
        {
            using Witness result = WindowAssembler.ToWitness(witness);
        })!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    public void Push_UnsupportedL1Evidence_DoesNotAdmitTheBlock()
    {
        ByteString hash = ByteString.CopyFrom(new byte[32]);
        WindowAssembler assembler = WindowAssembler.Start(WindowLimits.Default, new ProveChunk
        {
            Header = new ProveHeader { RollupId = 1, FromBlock = 1, ToBlock = 1, PostBatch = new PostBatch() }
        });
        ProveChunk chunk = new()
        {
            Block = new BlockWitness { Number = 1, Hash = hash, ParentHash = hash, Rlp = hash, Witness = new ExecutionWitness() }
        };
        chunk.Block.Witness.L1HeadersRlp.Add(ByteString.CopyFrom([0xc0]));
        try
        {
            Assert.That(Assert.Throws<RpcException>(() => assembler.Push(chunk))!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
            Assert.That(Assert.Throws<RpcException>(() => assembler.Finish())!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        }
        finally { assembler.Abandon(); }
    }

    [Test]
    public void AdmitBlock_UnsupportedL1Evidence_DoesNotChangeProgress([ValueSource(nameof(CurrentStreamingProfiles))] string profile)
    {
        ByteString hash = ByteString.CopyFrom(new byte[32]);
        Begin begin = new() { RollupId = 1, AnchorNumber = 0, AnchorHash = hash, AnchorStateRoot = hash };
        StreamingSession session = new("composer", begin, new StreamingSessionLimits(4, 10000, 32),
            new StreamingProtocolRules(Enum.Parse<StreamingSettlementProfile>(profile)), 128);
        BlockWitness block = new() { Number = 1, Hash = hash, ParentHash = hash, Rlp = hash, Witness = new ExecutionWitness() };
        block.Witness.L1HeadersRlp.Add(ByteString.CopyFrom([0xc0]));
        RpcException error = Assert.Throws<RpcException>(() => session.AdmitBlock(session.Binding, 1, block, 500))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));
            Assert.That(session.Ready(session.Binding, 2).Ready.ValidatedThrough, Is.Zero);
        }
    }
}
