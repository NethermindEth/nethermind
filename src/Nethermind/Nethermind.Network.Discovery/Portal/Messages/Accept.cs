// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class Accept
{
    public ushort ConnectionId { get; set; }

    // TODO: need to be bit array
    [SszList(256)]
    public BitArray AcceptedBits { get; set; } = new BitArray(0);
}
