// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Portal;

public interface IContentDistributor
{
    Task<int> DistributeContent(byte[] contentKey, byte[] content, CancellationToken token);
    Task OfferAndSendContent(IEnr enr, byte[] contentKey, byte[] content, CancellationToken token);
}
