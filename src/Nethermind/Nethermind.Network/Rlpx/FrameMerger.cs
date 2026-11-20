// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx;

/// <summary>Checks authenticated, decrypted RLPx frames and tracks the chunked packet in progress.</summary>
/// <remarks>
/// For each frame it says where the packet's content lies, and the caller copies or keeps the bytes in its own buffers.
/// After a failed check it drops the packet in progress. A caller that fails to store a chunk calls <see cref="Reset"/>.
/// </remarks>
internal sealed class FrameMerger
{
    private bool _inProgress;
    private int? _currentContextId;
    private int _remainingContentLength;

    /// <summary>Reads a frame: its decrypted header followed by its payload with padding.</summary>
    /// <exception cref="InvalidFrameException">The frame breaks the framing rules.</exception>
    public MergedFrame Read(ReadOnlySpan<byte> frame)
    {
        FrameHeaderReader.FrameInfo info = FrameHeaderReader.ReadFrameHeader(frame[..Frame.HeaderSize]);
        ReadOnlySpan<byte> payload = frame[Frame.HeaderSize..];
        if (payload.Length != info.PayloadSize)
            ThrowInvalidFrameLength(payload.Length, info.PayloadSize);

        if (!info.TotalPacketSize.HasValue && _inProgress)
        {
            if (info.ContextId != _currentContextId)
            {
                int? expectedContextId = _currentContextId;
                Reset();
                ThrowUnexpectedContextId(info.ContextId, expectedContextId);
            }

            if (info.Size > _remainingContentLength)
            {
                int remainingContentLength = _remainingContentLength;
                Reset();
                ThrowFrameSizeExceedsRemaining(info.Size, remainingContentLength);
            }

            _remainingContentLength -= info.Size;
            return ResetWhenComplete(new MergedFrame(MergedFrameKind.Chunk, 0, Frame.HeaderSize, info.Size, 0, _remainingContentLength == 0));
        }

        if (_inProgress)
        {
            // Offending frame is intentionally not processed; the exception propagates for the peer handler to classify.
            Reset();
            throw new InvalidFrameException($"{nameof(FrameMerger)} received a new first chunk before the in-progress packet completed");
        }

        ulong rlpPacketType = DecodePacketType(payload, out int read);

        if (read > info.Size)
            ThrowPacketTypeLengthExceedsFrameSize(read, info.Size);

        if (rlpPacketType > byte.MaxValue)
            ThrowPacketTypeOutOfRange(rlpPacketType);

        int contentOffset = Frame.HeaderSize + read;
        int contentLength = info.Size - read;
        if (!info.TotalPacketSize.HasValue)
        {
            return new MergedFrame(MergedFrameKind.SinglePacket, (byte)rlpPacketType, contentOffset, contentLength, contentLength, true);
        }

        int totalContentLength = info.TotalPacketSize.Value - read;
        _inProgress = true;
        _currentContextId = info.ContextId;
        _remainingContentLength = totalContentLength - contentLength;
        return ResetWhenComplete(new MergedFrame(MergedFrameKind.FirstChunk, (byte)rlpPacketType, contentOffset, contentLength, totalContentLength, _remainingContentLength == 0));
    }

    /// <summary>Drops the packet in progress.</summary>
    public void Reset()
    {
        _inProgress = false;
        _currentContextId = null;
        _remainingContentLength = 0;
    }

    private MergedFrame ResetWhenComplete(MergedFrame frame)
    {
        if (frame.IsComplete) Reset();
        return frame;
    }

    /// <remarks>
    /// The try/catch lives here rather than in <see cref="Read"/> so that the caller stays free of an
    /// exception handling region, which RyuJIT refuses to inline.
    /// </remarks>
    private static ulong DecodePacketType(ReadOnlySpan<byte> payload, out int read)
    {
        try
        {
            RlpReader reader = new(payload);
            ulong packetType = reader.DecodeULong();
            read = reader.BytesRead;
            return packetType;
        }
        catch (Exception exception) when (exception is RlpReaderException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidFrameException(exception);
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidFrameLength(int actual, int expected)
        => throw new InvalidFrameException($"Decoded frame has {actual} payload bytes, expected {expected}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowFrameSizeExceedsRemaining(int frameSize, int remainingPacketSize)
        => throw new InvalidFrameException(
            $"{nameof(FrameMerger)} frame size {frameSize} exceeds remaining packet size {remainingPacketSize}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowPacketTypeLengthExceedsFrameSize(int packetTypeLength, int frameSize)
        => throw new InvalidFrameException(
            $"{nameof(FrameMerger)} packet type length {packetTypeLength} exceeds frame size {frameSize}");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowPacketTypeOutOfRange(ulong packetType)
        => throw new InvalidFrameException(
            $"{nameof(FrameMerger)} packet type {packetType} does not fit in a byte");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnexpectedContextId(int? contextId, int? expectedContextId)
        => throw new InvalidFrameException(
            $"{nameof(FrameMerger)} continuation frame context id {contextId} does not match in-progress packet context id {expectedContextId}");
}

internal enum MergedFrameKind
{
    /// <summary>A packet in a single frame.</summary>
    SinglePacket,

    /// <summary>The first chunk of a chunked packet, which declares the packet's total size.</summary>
    FirstChunk,

    /// <summary>A later chunk of a chunked packet.</summary>
    Chunk
}

/// <summary>What a frame holds of a packet.</summary>
/// <param name="Kind">The kind of frame.</param>
/// <param name="PacketType">The packet type, read from a single packet or a first chunk.</param>
/// <param name="ContentOffset">Where the frame's share of the packet's content starts, from the start of the frame.</param>
/// <param name="ContentLength">The length of the frame's share of the packet's content, without padding.</param>
/// <param name="TotalContentLength">The length of the whole packet's content, without the packet type, for a single packet or a first chunk.</param>
/// <param name="IsComplete">Whether the packet is complete with this frame.</param>
internal readonly record struct MergedFrame(
    MergedFrameKind Kind,
    byte PacketType,
    int ContentOffset,
    int ContentLength,
    int TotalContentLength,
    bool IsComplete);
