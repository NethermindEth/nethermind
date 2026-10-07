// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Core;

public readonly partial struct StorageCell(Address address, in UInt256 index)
{
    private readonly AddressAsKey _address = address;
    [JsonInclude]
    public readonly UInt256 Index = index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetHashCode64() => _address.Value.GetHashCode64(in Index);
}
