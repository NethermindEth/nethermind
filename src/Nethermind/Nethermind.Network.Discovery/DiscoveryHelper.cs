// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;

namespace Nethermind.Network.Discovery;

public static class DiscoveryHelper
{
    private static readonly AttributeKey<string> MessageVersion = AttributeKey<string>.ValueOf("MessageVersion");

    public static void SetMessageVersion(this IChannelHandlerContext ctx, int version)
    {
        ctx.GetAttribute(MessageVersion).Set($"{version}");
    }

    public static bool HasDiscoveryMessageVersion(this IChannelHandlerContext ctx)
    {
        return !string.IsNullOrEmpty(ctx.GetAttribute(MessageVersion).Get());
    }
}
