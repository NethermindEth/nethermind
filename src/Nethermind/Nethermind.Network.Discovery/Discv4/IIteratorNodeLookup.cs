// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public interface IIteratorNodeLookup
{
    IAsyncEnumerable<Node> Lookup(PublicKey target, CancellationToken token);
}
