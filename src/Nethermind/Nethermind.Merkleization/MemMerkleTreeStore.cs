// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merkleization;

public class MemMerkleTreeStore : IKeyValueStore<ulong>
{
    private readonly Dictionary<ulong, byte[]?> _dictionary = new();

    public byte[]? this[ulong key]
    {
        get => _dictionary.GetValueOrDefault(key);
        set => _dictionary[key] = value;
    }
}
