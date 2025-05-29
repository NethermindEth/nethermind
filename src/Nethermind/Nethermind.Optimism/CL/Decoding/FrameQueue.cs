// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

public class FrameQueue(ILogManager logManager) : IFrameQueue
{
    private readonly List<byte> _frameData = [];
    private readonly ILogger _logger = logManager.GetClassLogger();

    private Frame? _latestFrame;

    // https://specs.optimism.io/protocol/holocene/derivation.html#frame-queue
    public BatchV1[]? ConsumeFrame(Frame frame)
    {
        if (frame.FrameNumber > 0)
        {
            // If a non-first frame (i.e., a frame with index >0)
            // decoded from a batcher transaction is out of order, it is immediately dropped
            if (_latestFrame is null || _latestFrame.Value.FrameNumber + 1 != frame.FrameNumber ||
                _latestFrame.Value.IsLast || _latestFrame.Value.ChannelId != frame.ChannelId)
            {
                if (_logger.IsWarn) _logger.Warn($"Got out of order frame. Number {frame.FrameNumber}, ChannelId {frame.ChannelId}");
                return null;
            }
        }
        else
        {
            // If a first frame is decoded while the previous frame isn't a last frame (i.e., is_last is false),
            // all previous frames for the same channel are dropped and this new first frame remains in the queue.
            if (_latestFrame is not null && !_latestFrame.Value.IsLast)
            {
                if (_logger.IsWarn) _logger.Warn($"Previous frame is dropped. New ChannelId {frame.ChannelId}");
                _frameData.Clear();
            }
        }

        _frameData.AddRange(frame.FrameData);
        _latestFrame = frame;

        if (frame.IsLast)
        {
            var decodedChannel = ChannelDecoder.DecodeChannel(_frameData.ToArray());
            _frameData.Clear();

            var rlp = new Rlp.ValueDecoderContext(decodedChannel.Span);
            var batchData = rlp.DecodeByteArrayMemory()!.Value;
            var batches = BatchDecoder.DecodeSpanBatches(batchData).ToArray();
            return batches;
        }

        return null;
    }
    public void Clear()
    {
        _latestFrame = null;

    }
}
