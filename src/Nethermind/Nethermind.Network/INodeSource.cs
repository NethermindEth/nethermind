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
    private static readonly char[] separator = ['\r', '\n'];

    IAsyncEnumerable<Node> DiscoverNodes(CancellationToken cancellationToken);

    event EventHandler<NodeEventArgs> NodeRemoved;

    static IEnumerable<string> ParseNodes(string data)
    {
        string[] nodes;
        try
        {
            nodes = JsonSerializer.Deserialize<string[]>(data) ?? [];
        }
        catch (JsonException)
        {
            nodes = data.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        }

        return nodes.Distinct();
    }
}
