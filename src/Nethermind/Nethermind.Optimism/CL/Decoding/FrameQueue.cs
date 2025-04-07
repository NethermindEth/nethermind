// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

public class FrameQueue : IFrameQueue
{
    private readonly Queue<BatchV1[]> _batches = new();

    private readonly List<byte> _frameData = new();
    private Frame? _latestFrame;

    // https://specs.optimism.io/protocol/holocene/derivation.html#frame-queue
    public void ConsumeFrame(Frame frame)
    {
        if (frame.FrameNumber > 0)
        {
            // If a non-first frame (i.e., a frame with index >0)
            // decoded from a batcher transaction is out of order, it is immediately dropped
            if (_latestFrame is null) return;
            if (_latestFrame.Value.FrameNumber + 1 != frame.FrameNumber) return;
            if (_latestFrame.Value.IsLast) return;
            if (_latestFrame.Value.ChannelId != frame.ChannelId) return;
        }
        else
        {
            // If a first frame is decoded while the previous frame isn't a last frame (i.e., is_last is false),
            // all previous frames for the same channel are dropped and this new first frame remains in the queue.
            if (_latestFrame is null || !_latestFrame.Value.IsLast)
            {
                _frameData.Clear();
            }
        }

        _frameData.AddRange(frame.FrameData);
        _latestFrame = frame;

        if (frame.IsLast)
        {
            var decodedChannel = ChannelDecoder.DecodeChannel(_frameData.ToArray());
            _frameData.Clear();

            var memory = new Memory<byte>(decodedChannel);
            var rlp = new Rlp.ValueDecoderContext(memory);
            var batchData = rlp.DecodeByteArrayMemory()!.Value;
            var batches = BatchDecoder.DecodeSpanBatches(batchData).ToArray();

            _batches.Enqueue(batches);
        }
    }

    public BatchV1[]? GetReadyBatches()
    {
        if (_batches.TryDequeue(out BatchV1[]? batches))
        {
            return batches;
        }

        return null;
    }
}
