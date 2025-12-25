// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.LogIndex;

internal class FastHashEqualityComparer: IEqualityComparer<Address>, IEqualityComparer<Hash256>
{
    public static readonly FastHashEqualityComparer Instance = new();

    private FastHashEqualityComparer() { }

    public bool Equals(Address? x, Address? y) => x?.Equals(y) ?? y is null;

    public int GetHashCode(Address? address) => address is null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(address.Bytes.AsSpan(^sizeof(int)..));

    public bool Equals(Hash256? x, Hash256? y) => x?.Equals(y) ?? y is null;

    public int GetHashCode(Hash256? topic) => topic is null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(topic.Bytes[^sizeof(int)..]);
}
