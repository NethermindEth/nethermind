// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Portal.History.Rpc.Model;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History;

public interface IPortalHistoryNetwork
{
    void AddEnr(IEnr enr);
    IEnr GetEnr(ValueHash256 nodeId);
    void DeleteEnr(ValueHash256 nodeId);
    Task<IEnr> LookupEnr(ValueHash256 nodeId, CancellationToken token);
    Task<Pong> Ping(IEnr enr, CancellationToken token);
    Task<IEnr[]> FindNodes(IEnr enr, ushort[] distances, CancellationToken token);
    Task<FindContentResult> FindContent(IEnr enr, string contentKey, CancellationToken token);
    Task<string> Offer(IEnr enr, string contentKey, string contentValue, CancellationToken token);
    Task<IEnr[]> LookupKNodes(ValueHash256 nodeId, CancellationToken token);
    Task<RecursiveFindContentResult> LookupContent(byte[] contentKey, CancellationToken token);
    Task<TraceRecursiveFindContentResult> TraceLookupContent(byte[] contentKey, CancellationToken token);
    void Store(byte[] contentKey, byte[] contentValue);
    byte[] LocalContent(byte[] contentKey);
    Task<int> Gossip(byte[] contentKey, byte[] contentValue, CancellationToken token);
}
