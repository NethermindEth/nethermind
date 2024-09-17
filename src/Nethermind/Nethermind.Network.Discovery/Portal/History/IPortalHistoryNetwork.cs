// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Portal.History;

public interface IPortalHistoryNetwork
{
    Task<BlockHeader?> LookupBlockHeader(ValueHash256 hash, CancellationToken token);
    Task<BlockBody?> LookupBlockBody(ValueHash256 hash, CancellationToken token);
    Task<BlockBody?> LookupBlockBodyFrom(IEnr enr, ValueHash256 hash, CancellationToken token);
    Task Run(CancellationToken token);
}
