// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;

namespace Nethermind.Network.Rlpx
{
    public interface IFramingAware : IChannelHandler
    {
        void DisableFraming();

        int MaxFrameSize { get; }
    }
}
