// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Transport.Channels;

namespace Nethermind.Network;

public interface IChannelFactory
{
    public IServerChannel CreateServer();

    public IChannel CreateClient();
}