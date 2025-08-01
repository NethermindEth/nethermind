// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Channels;

namespace Nethermind.Core.Extensions;

public static class ChannelExtensions
{
    public static List<T> ReadBatch<T>(this ChannelReader<T> channel, int batchSize)
    {
        if (!channel.TryPeek(out _))
            return [];

        var result = new List<T>(batchSize);
        while (result.Count < batchSize && channel.TryRead(out T? item))
            result.Add(item);

        return result;
    }
}
