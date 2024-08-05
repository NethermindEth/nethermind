// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Int256;

namespace Nethermind.Network.Discovery.Portal;

public interface IContentDistributor
{
    void UpdatePeerRadius(IEnr sender, UInt256 radius);
    Task DistributeContent(byte[] contentKey, byte[] content, CancellationToken token);
}
