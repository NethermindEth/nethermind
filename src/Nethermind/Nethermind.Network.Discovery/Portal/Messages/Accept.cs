// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;

namespace Nethermind.Network.Discovery.Portal.Messages;

public class Accept
{
    public ushort ConnectionId { get; set; }
    public BitArray AcceptedBits { get; set; } = new BitArray(0);
}
