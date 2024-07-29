// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal.Messages;

public class LookupContentResult
{
    public byte[]? Payload { get; set; }
    public ushort? ConnectionId { get; set; }

    // Used in case of utp transfer where the node id need to be known.
    public IEnr NodeId { get; set; } = null!;
}
