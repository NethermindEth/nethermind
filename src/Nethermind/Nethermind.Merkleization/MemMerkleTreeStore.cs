// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merkleization;

public class MemMerkleTreeStore : IKeyValueStore<ulong, byte[]>
{
    private Dictionary<ulong, byte[]?> _dictionary = new Dictionary<ulong, byte[]?>();

    public byte[]? this[ulong key]
    {
        get => _dictionary.TryGetValue(key, out byte[]? value) ? value : null;
        set => _dictionary[key] = value;
    }
}
