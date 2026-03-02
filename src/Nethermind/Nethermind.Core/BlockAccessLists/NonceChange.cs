// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct NonceChange(ushort BlockAccessIndex, ulong NewNonce) : IIndexedChange
{
    public override string ToString() => $"{BlockAccessIndex}:{NewNonce}";
}
