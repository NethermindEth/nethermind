// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

public class ChannelStorage : IChannelStorage
{
    private readonly ConcurrentDictionary<UInt128, IFrameQueue> _frameQueues = new();
    private readonly ConcurrentQueue<BatchV1[]> _batches = new();

    public void ConsumeFrame(Frame frame)
    {
        if (!_frameQueues.ContainsKey(frame.ChannelId))
        {
            _frameQueues.TryAdd(frame.ChannelId, new FrameQueue());
        }

        IFrameQueue queue = _frameQueues[frame.ChannelId];
        queue.ConsumeFrame(frame);
        if (queue.IsReady())
        {
            byte[] decompressed = ChannelDecoder.DecodeChannel(queue.BuildChannel());
            Rlp.ValueDecoderContext decoder = new(decompressed, sliceMemory: true);
            Memory<byte> batchData = decoder.DecodeByteArrayMemory()!.Value;
            BatchV1[] batches = BatchDecoder.DecodeSpanBatches(batchData).ToArray();
            _batches.Enqueue(batches);
            // TODO: we need to remove frames and do not allow to reuse channelId at the same time. Check the specs!
            //_frameQueues.Remove(frames[0].ChannelId);
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
