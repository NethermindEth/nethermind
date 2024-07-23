// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal;

public class Content: IUnion
{
    [Selector(0)]
    public ushort? ConnectionId { get; set; }

    [Selector(1)]
    public byte[]? Payload { get; set; }

    [Selector(2)]
    public byte[][]? Enrs { get; set; }
}
