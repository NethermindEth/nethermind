// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;
using System.Collections.Generic;

namespace Nethermind.Shutter;

[SszSerializable]
public struct SlotDecryptionIdentites
{
    public ulong InstanceID { get; set; }
    public ulong Eon { get; set; }
    public ulong Slot { get; set; }
    public ulong TxPointer { get; set; }

    [SszList(1024)]
    public List<IdentityPreimage> IdentityPreimages { get; set; }
}

[SszSerializable]
public struct IdentityPreimage(byte[] data)
{
    [SszVector(52)]
    public byte[] Data { get; set; } = data;
}
