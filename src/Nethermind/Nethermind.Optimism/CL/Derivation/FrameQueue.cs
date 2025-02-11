// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.CL.Derivation;

// Compressed batches of L2 txs are split into multiple frames.
// This class is used to merge these frames back together
// Workflow should look like this:
// ConsumeFrame(frame)
// IsReady() - false
// ConsumeFrame(frame)
// IsReady() - true
// BuildChannel() - merges all frames together and returns compressed data
public class FrameQueue : IFrameQueue
{
    // Make thread safe
    private UInt128? _channelId;
    private ushort? _numberOfFrames;
    private readonly Dictionary<ushort, Frame> _frames = new();

    public void ConsumeFrame(Frame frame)
    {
        if (_channelId is null)
        {
            _channelId = frame.ChannelId;
        }
        else if (_channelId != frame.ChannelId)
        {
            throw new ArgumentException(
                $"Frame with wrong ChannelId. Expected: {_channelId}, got {frame.ChannelId}");
        }

        if (frame.IsLast)
        {
            if (_numberOfFrames is not null)
            {
                throw new ArgumentException($"Multiple last frames in a channel: {_channelId}");
            }

            // TODO: limit number of frames
            _numberOfFrames = (ushort)(frame.FrameNumber + 1);
        }

        if (frame.FrameNumber > _numberOfFrames)
        {
            throw new ArgumentException($"Inconsistent frame number in a channel: {_channelId}");
        }

        if (_frames.ContainsKey(frame.FrameNumber))
        {
            throw new ArgumentException($"Duplicate frame number in a channel: {_channelId}");
        }

        _frames.Add(frame.FrameNumber, frame);
    }

    public bool IsReady()
    {
        return _frames.Count == _numberOfFrames;
    }

    // TODO: do not merge frames to save memory
    public byte[] BuildChannel()
    {
        if (!IsReady())
        {
            throw new InvalidOperationException(
                $"Channel is not ready. Frames consumed: {_frames.Count}, expected: {_numberOfFrames}");
        }
        List<byte> result = new();
        for (ushort i = 0; i < _numberOfFrames; i++)
        {
            result.AddRange(_frames[i].FrameData);
        }
        return result.ToArray();
    }
}
