// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>An in-memory <see cref="INativeNodeStore"/> over <paramref name="nodes"/>, keyed by path alone.</summary>
public sealed class DictionaryNodeStore(IReadOnlyDictionary<TreePath, byte[]> nodes) : INativeNodeStore
{
    public bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver)
    {
        if (!nodes.TryGetValue(path, out byte[]? node)) return false;
        receiver.Receive(node);
        return true;
    }
}
