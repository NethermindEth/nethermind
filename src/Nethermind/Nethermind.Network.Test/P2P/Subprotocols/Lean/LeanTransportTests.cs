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
    public void Chunks_reassemble_sequentially_and_deduplicate([Values(32, 64, 128)] int chunkKiB)
    {
        int size = chunkKiB * 1024;
        byte[] payload = new byte[size * 3 + 17];
        new Random(2718).NextBytes(payload);
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        LeanProofChunkMessageSerializer serializer = new();
        int[] indices = [0, 1, 1, 2, 3];
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
            Assert.That(budget.RetainedBytes, Is.EqualTo(2 * (fragment.Length + count * 16)));
        }
        reassembler.Dispose();
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Advertised_large_objects_charge_only_received_memory_and_leave_capacity_for_other_peers()
    {
        LeanReassemblyBudget budget = new();
        List<LeanChunkReassembler> peers = [];
        byte[] fragment = new byte[16 * 1024];
        int total = LeanProofStore.MaxWrapperBytes;
        int count = total / fragment.Length;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                LeanChunkReassembler peer = new(budget);
                peers.Add(peer);
                for (int stream = 0; stream < 2; stream++)
                    Assert.That(peer.Add(new(ValueKeccak.Compute($"{i}:{stream}"), total, 0,
                        count, fragment.Length, fragment)), Is.Null);
            }
            Assert.That(budget.RetainedBytes, Is.EqualTo(8 * (fragment.Length + count * 16)));
            using LeanChunkReassembler honest = new(budget);
            using LeanProofWrapperMessage? admitted = honest.Add(Chunk([1], 0));
            Assert.That(admitted, Is.Not.Null);
        }
        finally { foreach (LeanChunkReassembler peer in peers) peer.Dispose(); }
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Missing_start_is_ignored_and_an_active_stream_rejects_gaps_without_growing()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget);
        byte[] payload = new byte[3 * LeanProofChunkMessage.DefaultChunkSize];
        Assert.That(peer.Add(Chunk(payload, 1)), Is.Null);
        Assert.That(budget.RetainedBytes, Is.Zero);
        peer.Add(Chunk(payload, 0));
        Assert.Throws<RlpException>(() => peer.Add(Chunk(payload, 2)));
        Assert.That(budget.RetainedBytes, Is.Zero, "malformed stream releases its fragments");
    }

    [Test]
    public void Local_growth_pressure_releases_stream_and_later_chunks_cannot_open_it()
    {
        ManualTimeProvider clock = new();
        int penalties = 0;
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget, clock, () => penalties++);
        byte[] payload = new byte[3 * LeanProofChunkMessage.DefaultChunkSize];
        peer.Add(Chunk(payload, 0));
        using IDisposable pressure = budget.TryRent(LeanReassemblyBudget.MaxBytes - budget.RetainedBytes, _ => { }, incomplete: false)!;
        Assert.That(peer.Add(Chunk(payload, 1)), Is.Null);
        Assert.That(budget.RetainedAssemblies, Is.EqualTo(1), "only the unrelated pressure lease remains");
        Assert.That(peer.Add(Chunk(payload, 2)), Is.Null);
        pressure.Dispose();
        clock.AdvanceAndFireTimer(LeanChunkReassembler.MaxAssemblyLifetime);
        Assert.That(penalties, Is.Zero);
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Completion_copy_is_reserved_before_allocation_and_pressure_releases_all_fragments()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget);
        byte[] payload = new byte[2 * LeanProofChunkMessage.DefaultChunkSize];
        peer.Add(Chunk(payload, 0));
        int availableForLastChunk = LeanProofChunkMessage.DefaultChunkSize;
        using IDisposable pressure = budget.TryRent(LeanReassemblyBudget.MaxBytes - budget.RetainedBytes - availableForLastChunk, _ => { }, incomplete: false)!;
        Assert.That(peer.Add(Chunk(payload, 1)), Is.Null);
        Assert.That(budget.RetainedAssemblies, Is.EqualTo(1));
        pressure.Dispose();
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Two_maximum_streams_fit_the_wire_rate_budget_including_all_metadata()
    {
        int chunks = 2 * LeanProofStore.MaxWrapperBytes / (16 * 1024);
        int bytes = 2 * LeanProofStore.MaxWrapperBytes + chunks * LeanProofChunkMessage.HeaderSize;
        Assert.That(chunks, Is.LessThanOrEqualTo(LeanProtocolHandler.MaxChunksPerWindow));
        Assert.That(bytes, Is.LessThanOrEqualTo(LeanProtocolHandler.MaxWireBytesPerWindow));
    }

    [Test]
    public void Two_maximum_wrappers_complete_while_the_first_admission_lease_is_retained()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget);
        byte[] payload = new byte[LeanProofStore.MaxWrapperBytes];
        using LeanProofWrapperMessage? first = CompleteStream(peer, payload);
        Assert.That(first, Is.Not.Null);
        payload[0] = 1;
        using LeanProofWrapperMessage? second = CompleteStream(peer, payload);
        Assert.That(second, Is.Not.Null);
        Assert.That(budget.RetainedBytes, Is.EqualTo(2 * payload.Length));
        Assert.That(peer.Add(Chunk([3], 0)), Is.Null);
    }

    [Test]
    public void Actual_partial_data_leaves_headroom_for_fresh_small_wrappers_and_completion()
    {
        LeanReassemblyBudget budget = new();
        List<LeanChunkReassembler> peers = [];
        byte[] payload = new byte[LeanProofStore.MaxWrapperBytes];
        int size = LeanProofChunkMessage.DefaultChunkSize;
        int count = payload.Length / size;
        ValueHash256 hash = ValueKeccak.Compute(payload);
        try
        {
            for (int p = 0; p < 5; p++)
            {
                LeanChunkReassembler peer = new(budget);
                peers.Add(peer);
                int received = p == 4 ? 131 : count - 1;
                for (int index = 0; index < received; index++)
                    Assert.That(peer.Add(new(hash, payload.Length, index, count, size,
                        payload.AsMemory(index * size, size))), Is.Null);
            }
            Assert.That(budget.RetainedBytes,
                Is.InRange(LeanReassemblyBudget.MaxIncompleteBytes - 128 * 1024, LeanReassemblyBudget.MaxIncompleteBytes));
            using LeanChunkReassembler fresh = new(budget);
            using LeanProofWrapperMessage? small = fresh.Add(Chunk([1], 0));
            Assert.That(small, Is.Not.Null);
            using LeanProofWrapperMessage? completed = peers[0].Add(new(hash, payload.Length, count - 1, count,
                size, payload.AsMemory(payload.Length - size, size)));
            Assert.That(completed, Is.Not.Null, "reserved headroom permits a real contiguous completion copy");
            Assert.That(budget.RetainedBytes, Is.LessThanOrEqualTo(LeanReassemblyBudget.MaxBytes));
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
                admitted.Add(peer.Add(Chunk(BitConverter.GetBytes(i), 0))!);
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
    public void Repeated_incomplete_stream_expiry_reports_abuse_and_releases_quota()
    {
        ManualTimeProvider clock = new();
        int penalties = 0;
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget, clock, () => penalties++);
        byte[] payload = new byte[2 * LeanProofChunkMessage.DefaultChunkSize];
        peer.Add(Chunk(payload, 0));
        clock.AdvanceAndFireTimer(LeanChunkReassembler.InactivityTimeout);
        Assert.That(penalties, Is.Zero);
        Assert.That(budget.RetainedBytes, Is.Zero);
        peer.Add(Chunk(payload, 0));
        clock.AdvanceAndFireTimer(LeanChunkReassembler.InactivityTimeout);
        Assert.That(penalties, Is.EqualTo(1));
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Isolated_stalled_transfers_do_not_accumulate_strikes_forever()
    {
        ManualTimeProvider clock = new();
        int penalties = 0;
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget, clock, () => penalties++);
        byte[] payload = new byte[2 * LeanProofChunkMessage.DefaultChunkSize];
        peer.Add(Chunk(payload, 0));
        clock.AdvanceAndFireTimer(LeanChunkReassembler.InactivityTimeout);
        clock.AdvanceAndFireTimer(LeanChunkReassembler.AbandonmentWindow);
        peer.Add(Chunk(payload, 0));
        clock.AdvanceAndFireTimer(LeanChunkReassembler.InactivityTimeout);
        Assert.That(penalties, Is.Zero);
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    [Test]
    public void Slow_streams_cannot_reset_abandonment_strikes_between_absolute_expiries()
    {
        ManualTimeProvider clock = new();
        int penalties = 0;
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget, clock, () => penalties++);
        byte[] fragment = new byte[LeanProofChunkMessage.DefaultChunkSize];
        int total = LeanProofStore.MaxWrapperBytes;
        for (int stream = 0; stream < 2; stream++)
        {
            for (int index = 0; index < 15; index++)
            {
                peer.Add(new(default, total, index, total / fragment.Length, fragment.Length, fragment));
                clock.Advance(TimeSpan.FromSeconds(20));
            }
            clock.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
            Assert.That(budget.RetainedBytes, Is.Zero);
            Assert.That(penalties, Is.EqualTo(stream));
        }
    }

    [Test]
    public void Chunk_serialization_does_not_allocate_a_temporary_large_object()
    {
        byte[] payload = new byte[LeanProofChunkMessage.MaxChunkSize];
        LeanProofChunkMessage message = Chunk(payload, 0, payload.Length);
        LeanProofChunkMessageSerializer serializer = new();
        using DisposableByteBuffer buffer = Unpooled.Buffer(payload.Length + LeanProofChunkMessage.HeaderSize).AsDisposable();
        serializer.Serialize(buffer, message);
        buffer.Clear();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            serializer.Serialize(buffer, message);
            buffer.Clear();
        }
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - start, Is.LessThan(8 * 1024));
    }

    [Test]
    public void Admission_cancellation_can_retry_the_same_completed_commitment()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler peer = new(budget);
        using (LeanProofWrapperMessage? first = peer.Add(Chunk([1], 0)))
            Assert.That(first, Is.Not.Null);
        using LeanProofWrapperMessage? retry = peer.Add(Chunk([1], 0));
        Assert.That(retry, Is.Not.Null);
    }

    [Test]
    public void Failed_commitment_releases_the_completed_buffer_lease()
    {
        LeanReassemblyBudget budget = new();
        using LeanChunkReassembler reassembler = new(budget);
        Assert.Throws<RlpException>(() => reassembler.Add(new(default, 1, 0, 1, LeanProofChunkMessage.DefaultChunkSize, new byte[] { 1 })));
        Assert.That(budget.RetainedBytes, Is.Zero);
    }

    private static LeanProofWrapperMessage? CompleteStream(LeanChunkReassembler peer, byte[] bytes)
    {
        int size = LeanProofChunkMessage.DefaultChunkSize;
        int count = (bytes.Length + size - 1) / size;
        ValueHash256 hash = ValueKeccak.Compute(bytes);
        LeanProofWrapperMessage? completed = null;
        for (int index = 0; index < count; index++)
            completed = peer.Add(new(hash, bytes.Length, index, count, size,
                bytes.AsMemory(index * size, Math.Min(size, bytes.Length - index * size))));
        return completed;
    }

    private static LeanProofChunkMessage Chunk(byte[] bytes, int index, int chunkSize = LeanProofChunkMessage.DefaultChunkSize)
    {
        int offset = index * chunkSize;
        return new(ValueKeccak.Compute(bytes), bytes.Length, index, (bytes.Length + chunkSize - 1) / chunkSize,
            chunkSize, bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset)));
    }

}
