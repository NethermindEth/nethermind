// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Network.Discovery.Portal.Messages;

[SszSerializable]
public class Content
{
    public ContentType Selector { get; set; }

    public ushort ConnectionId { get; set; }

    [SszList(64000)] // TODO: Check limit
    public byte[]? Payload { get; set; }

    [SszList(64000)] // TODO: Check limit
    public Enr[]? Enrs { get; set; }
}

public enum ContentType
{
    ConnectionId = 0,
    Payload = 1,
    Enrs = 2,
}
