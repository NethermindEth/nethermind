// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Optimism.CL.Derivation;

public class ChannelStorage : IChannelStorage
{
    private readonly Dictionary<UInt128, IFrameQueue> _frameQueues = new();

    public void ConsumeFrames(Frame[] frames)
    {
        foreach (Frame frame in frames)
        {
            if (!_frameQueues.ContainsKey(frame.ChannelId))
            {
                _frameQueues.Add(frame.ChannelId, new FrameQueue());
            }

            IFrameQueue queue = _frameQueues[frame.ChannelId];
            queue.ConsumeFrame(frame);
            if (queue.IsReady())
            {
                OnChannelBuilt?.Invoke(queue.BuildChannel());
                // TODO: we need to remove frames and do not allow to reuse channelId at the same time. Check the specs!
                //_frameQueues.Remove(frames[0].ChannelId);
            }
        }
    }

    public event Action<byte[]>? OnChannelBuilt;
}
