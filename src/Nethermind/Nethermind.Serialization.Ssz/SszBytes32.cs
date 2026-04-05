// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Ssz;

public readonly struct SszBytes32
{
    private readonly ValueHash256 _value;

    public SszBytes32(ReadOnlySpan<byte> bytes) => _value = new ValueHash256(bytes);

    public ValueHash256 Hash => _value;

    public static SszBytes32 From(ValueHash256 hash) => new(hash.Bytes);
}
