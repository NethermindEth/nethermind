// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.History.Proofs;

internal readonly struct TrieLeaf(in ValueHash256 path, byte[] value)
{
    public ValueHash256 Path { get; } = path;

    public byte[] Value { get; } = value;
}
