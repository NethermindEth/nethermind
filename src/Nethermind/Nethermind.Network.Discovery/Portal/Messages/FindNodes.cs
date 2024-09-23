// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class FindNodes
{
    [SszList(256)]
    public ushort[] Distances { get; set; } = null!;
}
