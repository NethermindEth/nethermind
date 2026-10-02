// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

[NonParallelizable]
public class LeanTransportTests
{
    [Test]
    public void Maximum_wrapper_roundtrips_through_compressed_encrypted_frames()
    {
        Assert.That(Snappy.GetMaxCompressedLength(LeanProofStore.MaxWrapperBytes)
            + Rlp.LengthOf(ulong.MaxValue) + Frame.HeaderSize + 2 * Frame.MacSize + Frame.BlockSize - 1,
            Is.LessThanOrEqualTo(ZeroFrameDecoder.DefaultMaxInboundFrameSize));
        byte[] payload = new byte[LeanProofStore.MaxWrapperBytes];
        new Random(17342).NextBytes(payload);
        LeanProofWrapperMessageSerializer serializer = new();
        (EncryptionSecrets a, EncryptionSecrets b) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor outboundMac = new(TestItem.IgnoredPublicKey, a);
        using FrameMacProcessor inboundMac = new(TestItem.IgnoredPublicKey, b);
        ZeroPacketSplitter splitter = new(new FrameCipher(a.AesSecret), outboundMac);
        splitter.EnableSnappy(LimboLogs.Instance);
        EmbeddedChannel outbound = new(splitter);
        ISession session = Substitute.For<ISession>();
        ZeroPacket received = null;
        session.When(s => s.ReceiveMessage(Arg.Any<ZeroPacket>())).Do(call =>
        {
            received = call.Arg<ZeroPacket>();
            received.Retain();
        });
        ZeroNettyP2PHandler handler = new(session, LimboLogs.Instance);
        handler.EnableSnappy();
        EmbeddedChannel inbound = new(new ZeroFrameDecoder(new FrameCipher(b.AesSecret), inboundMac),
            new ZeroFrameMerger(LimboLogs.Instance), handler);
        try
        {
            IByteBuffer input = Unpooled.Buffer(payload.Length + 1);
            input.WriteByte(1);
            serializer.Serialize(input, new LeanProofWrapperMessage(payload));
            outbound.WriteOutbound(input);
            IByteBuffer wire = outbound.ReadOutbound<IByteBuffer>();
            Assert.That(wire.ReadableBytes, Is.LessThan(ZeroFrameDecoder.DefaultMaxInboundFrameSize));
            inbound.WriteInbound(wire);
            Assert.That(received, Is.Not.Null);
            Assert.That(received.PacketType, Is.EqualTo(1));
            Assert.That(serializer.Deserialize(received.Content).Wrapper.AsSpan(), Is.SequenceEqualTo(payload));
            session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            received?.Release();
            outbound.FinishAndReleaseAll();
            inbound.FinishAndReleaseAll();
        }
    }

    [Test]
    public void Oversized_wrapper_is_rejected_before_copy_or_serialization()
    {
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(new byte[LeanProofStore.MaxWrapperBytes + 1]).AsDisposable();
        LeanProofWrapperMessageSerializer serializer = new();
        Assert.Throws<RlpException>(() => serializer.Deserialize(buffer));
        Assert.That(buffer.ReaderIndex, Is.Zero);
        buffer.Clear();
        Assert.Throws<ArgumentException>(() => serializer.Serialize(buffer,
            new LeanProofWrapperMessage(new byte[LeanProofStore.MaxWrapperBytes + 1])));
        Assert.That(buffer.WriterIndex, Is.Zero);
    }
    [Test]
    public void Chunks_reassemble_out_of_order_and_deduplicate([Values(32, 64, 128)] int chunkKiB)
    {
        int size = chunkKiB * 1024;
        byte[] payload = new byte[size * 3 + 17];
        new Random(2718).NextBytes(payload);
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        LeanProofChunkMessageSerializer serializer = new();
        int[] indices = [3, 1, 1, 0, 2];
        LeanProofWrapperMessage? result = null;
        foreach (int index in indices)
        {
            using DisposableByteBuffer buffer = Unpooled.Buffer().AsDisposable();
            serializer.Serialize(buffer, Chunk(payload, index, size));
            Assert.That(buffer.ReadableBytes, Is.LessThanOrEqualTo(size + LeanProofChunkMessage.HeaderSize));
            result = reassembler.Add(serializer.Deserialize(buffer));
        }
        Assert.That(result, Is.Not.Null);
        using (result)
        {
            Assert.That(result!.Wrapper.AsSpan(), Is.SequenceEqualTo(payload));
            Assert.That(budget.RetainedBytes, Is.EqualTo(payload.Length), "completed admission keeps its lease");
            Assert.That(reassembler.Add(Chunk(payload, 0, size)), Is.Null);
        }
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Malformed_chunk_geometry_is_rejected_before_payload_copy([Values(-1, 0, int.MaxValue)] int badTotal)
    {
        using DisposableByteBuffer buffer = Unpooled.Buffer(LeanProofChunkMessage.HeaderSize + 1).AsDisposable();
        byte[] bytes = new byte[LeanProofChunkMessage.HeaderSize + 1];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(32), badTotal);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(44), LeanProofChunkMessage.DefaultChunkSize);
        buffer.WriteBytes(bytes);
        Assert.Throws<RlpException>(() => new LeanProofChunkMessageSerializer().Deserialize(buffer));
        Assert.That(buffer.ReaderIndex, Is.Zero);
    }

    [Test]
    public void New_roots_do_not_replace_partial_assemblies_or_allocate_when_full()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        byte[] fragment = new byte[LeanProofChunkMessage.DefaultChunkSize];
        int total = LeanProofStore.MaxWrapperBytes;
        int count = total / fragment.Length;
        for (int i = 0; i < 100; i++)
            Assert.That(reassembler.Add(new(ValueKeccak.Compute($"root:{i}"), total, 0, count, fragment.Length, fragment)), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(2));
            Assert.That(budget.RetainedBytes, Is.EqualTo(2 * total));
        }
        reassembler.Dispose();
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Shared_byte_budget_bounds_many_peers_and_recovers_on_dispose()
    {
        LeanReassemblyBudget budget = new();
        List<LeanChunkReassembler> peers = [];
        byte[] fragment = new byte[LeanProofChunkMessage.DefaultChunkSize];
        LeanProofChunkMessage chunk = new(default, LeanProofStore.MaxWrapperBytes, 0,
            LeanProofStore.MaxWrapperBytes / fragment.Length, fragment.Length, fragment);
        try
        {
            for (int i = 0; i < 7; i++)
            {
                LeanChunkReassembler peer = new(budget);
                peers.Add(peer);
                Assert.That(peer.Add(chunk), Is.Null);
            }
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(6));
            Assert.That(budget.RetainedBytes, Is.EqualTo(6 * LeanProofStore.MaxWrapperBytes));
            peers[0].Dispose();
            peers[6].Add(chunk);
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(6));
        }
        finally { foreach (LeanChunkReassembler peer in peers) peer.Dispose(); }
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Completed_wrapper_leases_enforce_peer_and_global_object_budgets()
    {
        LeanReassemblyBudget budget = new();
        List<LeanChunkReassembler> peers = [];
        List<LeanProofWrapperMessage> admitted = [];
        try
        {
            for (int i = 0; i < LeanReassemblyBudget.MaxAssemblies; i++)
            {
                LeanChunkReassembler peer = new(budget);
                peers.Add(peer);
                admitted.Add(peer.Add(Chunk([(byte)i], 0))!);
            }
            using LeanChunkReassembler blocked = new(budget);
            Assert.That(blocked.Add(Chunk([255], 0)), Is.Null);
            admitted[0].Dispose();
            using LeanProofWrapperMessage? resumed = blocked.Add(Chunk([255], 0));
            Assert.That(resumed, Is.Not.Null);
            Assert.That(blocked.Add(Chunk([254], 0)), Is.Null, "global count remains full during admission");
        }
        finally
        {
            foreach (LeanProofWrapperMessage wrapper in admitted) wrapper.Dispose();
            foreach (LeanChunkReassembler peer in peers) peer.Dispose();
        }
        Assert.That(budget.RetainedAssemblies, Is.Zero);
    }

    [Test]
    public void Peer_slots_include_completed_wrappers_until_admission_releases_them()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        using LeanProofWrapperMessage? first = reassembler.Add(Chunk([1], 0));
        using LeanProofWrapperMessage? second = reassembler.Add(Chunk([2], 0));
        Assert.That(reassembler.Add(Chunk([3], 0)), Is.Null);
        first!.Dispose();
        using LeanProofWrapperMessage? third = reassembler.Add(Chunk([3], 0));
        Assert.That(third, Is.Not.Null);
        Assert.That(budget.RetainedAssemblies, Is.EqualTo(2));
    }

    [Test]
    public void Duplicate_fragments_do_not_extend_idle_expiry()
    {
        ManualTimeProvider clock = new();
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget, clock);
        byte[] payload = new byte[2 * LeanProofChunkMessage.DefaultChunkSize];
        LeanProofChunkMessage first = Chunk(payload, 0);
        reassembler.Add(first);
        clock.Advance(TimeSpan.FromSeconds(20));
        reassembler.Add(first);
        clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(11));
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Progress_cannot_pin_an_assembly_past_absolute_lifetime()
    {
        ManualTimeProvider clock = new();
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget, clock);
        byte[] fragment = new byte[LeanProofChunkMessage.DefaultChunkSize];
        int total = LeanProofStore.MaxWrapperBytes;
        for (int index = 0; index < 15; index++)
        {
            reassembler.Add(new(default, total, index, total / fragment.Length, fragment.Length, fragment));
            clock.Advance(TimeSpan.FromSeconds(20));
        }
        clock.AdvanceAndFireTimer(TimeSpan.Zero);
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Failed_commitment_releases_the_completed_buffer_lease()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        Assert.Throws<RlpException>(() => reassembler.Add(new(default, 1, 0, 1, LeanProofChunkMessage.DefaultChunkSize, new byte[] { 1 })));
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    private static LeanProofChunkMessage Chunk(byte[] bytes, int index, int chunkSize = LeanProofChunkMessage.DefaultChunkSize)
    {
        int offset = index * chunkSize;
        return new(ValueKeccak.Compute(bytes), bytes.Length, index, (bytes.Length + chunkSize - 1) / chunkSize,
            chunkSize, bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset)));
    }

}
