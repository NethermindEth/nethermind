// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

public sealed partial class LeafData
{
    public byte[]? Key { get; set; }

    public LeafData CloneWithNewValue(CappedArray<byte> value) => new(Key, value, StorageRoot);
}
