// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Verkle.Tree.Sync;

public class LeafToRefreshRequest
{
    /// <summary>
    ///     Root hash of the account trie to serve
    /// </summary>
    public Hash256 RootHash { get; set; }

    public byte[][] Paths { get; set; }

    public override string ToString()
    {
        return $"LeafToRefreshRequest: ({RootHash}, {Paths.Length})";
    }
}
