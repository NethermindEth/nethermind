// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Shutter;

[SszContainer]
public partial struct SlotDecryptionIdentities
{
    public ulong InstanceID { get; set; }
    public ulong Eon { get; set; }
    public ulong Slot { get; set; }
    public ulong TxPointer { get; set; }

    [SszList(1024)]
    public ArrayPoolList<IdentityPreimage> IdentityPreimages { get; set; }
}

[SszContainer]
public partial struct IdentityPreimage(ReadOnlyMemory<byte> data)
{
    [SszVector(52)]
    public ReadOnlyMemory<byte> Data { get; set; } = data;
}
