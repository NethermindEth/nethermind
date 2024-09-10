// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class Nodes
{
    public byte Total { get; set; } = 1;

    [SszList(64000)] // TODO: Check limit
    public Enr[] Enrs { get; set; } = null!;
}



[SszSerializable]
public class Enr
{
    [SszList(1024)]
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
