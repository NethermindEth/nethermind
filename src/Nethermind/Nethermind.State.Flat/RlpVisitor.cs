// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public delegate void RlpVisitor<TKey>(in TKey key, bool emptyUnknown, ReadOnlySpan<byte> rlp)
    where TKey : struct, IEquatable<TKey>;
