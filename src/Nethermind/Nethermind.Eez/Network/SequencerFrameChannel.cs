// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Libp2p.Core;
using GossipRpc = Nethermind.Libp2p.Protocols.Pubsub.Dto.Rpc;

namespace Nethermind.Eez.Network;

internal sealed class SequencerFrameChannel(IChannel inner, string? topic = null) : IChannel
{
    public async Task<int> ReadVarintAsync(CancellationToken token = default)
    {
        uint length = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            ReadOnlySequence<byte> next = await inner.ReadAsync(1, token: token).OrThrow();
            byte value = next.FirstSpan[0];
            if (shift == 28 && (value & 0xf0) != 0) break;
            length |= (uint)(value & 0x7f) << shift;
            if (length > SequencerHeadCodec.MaxFrameSize) break;
            if ((value & 0x80) == 0) return (int)length;
        }
        await inner.CloseAsync();
        throw new InvalidDataException("EEZ gossip frame length is invalid or exceeds its size limit.");
    }

    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default) => (uint)length > SequencerHeadCodec.MaxFrameSize
        ? RejectRead()
        : inner.ReadAsync(length, blockingMode, token);

    private async ValueTask<ReadResult> RejectRead()
    {
        await inner.CloseAsync();
        throw new InvalidDataException("EEZ gossip frame exceeds its size limit.");
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) =>
        bytes.Length > SequencerHeadCodec.MaxFrameSize + 5L ? RejectWrite() : inner.WriteAsync(bytes, token);

    private async ValueTask<IOResult> RejectWrite()
    {
        await inner.CloseAsync();
        throw new InvalidDataException("EEZ gossip frame exceeds its size limit.");
    }

    public async ValueTask WriteSizeAndProtobufAsync<T>(T message) where T : IMessage<T>
    {
        int length = message.CalculateSize();
        if ((uint)length > SequencerHeadCodec.MaxFrameSize)
        {
            await inner.CloseAsync();
            throw new InvalidDataException("EEZ gossip frame exceeds its size limit.");
        }
        byte[] frame = new byte[VarInt.GetSizeInBytes(length) + length];
        int offset = 0;
        VarInt.Encode(length, frame, ref offset);
        message.WriteTo(frame.AsSpan(offset));
        await inner.WriteAsync(new ReadOnlySequence<byte>(frame));
    }

    public ValueTask<T> ReadPrefixedProtobufAsync<T>(MessageParser<T> parser, CancellationToken token = default)
        where T : IMessage<T> => ReadPrefixedProtobufAsync(parser, SequencerHeadCodec.MaxFrameSize, token);

    public async ValueTask<T> ReadPrefixedProtobufAsync<T>(MessageParser<T> parser, int maxMessageLength,
        CancellationToken token) where T : IMessage<T>
    {
        int length = await ReadVarintAsync(token);
        if (length > maxMessageLength)
        {
            await inner.CloseAsync();
            throw new InvalidDataException("EEZ gossip frame exceeds its size limit.");
        }
        ReadOnlySequence<byte> payload = await ReadAsync(length, token: token).OrThrow();
        T message = parser.ParseFrom(payload);
        if (message is GossipRpc rpc && !IsAllowed(rpc))
        {
            await inner.CloseAsync();
            throw new InvalidDataException("EEZ gossip control message is invalid.");
        }
        return message;
    }

    private bool IsAllowed(GossipRpc rpc)
    {
        foreach (GossipRpc.Types.SubOpts subscription in rpc.Subscriptions)
            if (topic is not null && subscription.Topicid != topic) return false;
        if (rpc.Control is not { } control) return true;
        foreach (ControlIHave have in control.Ihave)
        {
            if (topic is not null && have.TopicID != topic) return false;
            foreach (ByteString id in have.MessageIDs)
                if (id.Length != 20) return false;
        }
        foreach (ControlIWant want in control.Iwant)
            foreach (ByteString id in want.MessageIDs)
                if (id.Length != 20) return false;
        foreach (ControlIDontWant unwanted in control.Idontwant)
            foreach (ByteString id in unwanted.MessageIDs)
                if (id.Length != 20) return false;
        foreach (ControlGraft graft in control.Graft)
            if (topic is not null && graft.TopicID != topic) return false;
        foreach (ControlPrune prune in control.Prune)
            if (topic is not null && prune.TopicID != topic) return false;
        return true;
    }

    public ValueTask AbortAsync() => inner.AbortAsync();
    public ValueTask CloseReadAsync() => inner.CloseReadAsync();
    public CancellationToken CancellationToken => inner.CancellationToken;

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
    public ValueTask CloseAsync() => inner.CloseAsync();
    public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
}
