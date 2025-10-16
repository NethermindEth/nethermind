// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Nethermind.Network;

public interface INodeSource
{
    private static readonly char[] _separator = ['\r', '\n'];

    IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken);

    event EventHandler<NodeEventArgs> NodeRemoved;

    static IEnumerable<string> ParseNodes(string data)
    {
        IEnumerable<string>? nodes;

        try
        {
            nodes = JsonSerializer.Deserialize<HashSet<string>>(data);
        }
        catch (JsonException)
        {
            nodes = data.Split(_separator, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }

        return nodes ?? [];
    }
}
