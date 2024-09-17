// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Network.Discovery.Portal.History.Rpc.Model;

namespace Nethermind.Network.Discovery.Portal.History.Rpc;

[RpcModule(ModuleType.PortalHistory)]
public interface IPortalHistoryRpcModule: IRpcModule
{
    [JsonRpcMethod(Description = "Returns ENR and nodeId information of the local discv5 node.")]
    ResultWrapper<NodeInfo> discv5_nodeInfo();

    [JsonRpcMethod(Description = "Returns meta information about history network routing table.")]
    ResultWrapper<RoutingTableInfoResult> portal_historyRoutingTableInfo();

    [JsonRpcMethod(Description = "Write an ethereum node record to the routing table.")]
    ResultWrapper<bool> portal_historyAddEnr(string enr);

    [JsonRpcMethod(Description = "Fetch from the local node the latest ENR associated with the given NodeId")]
    ResultWrapper<string> portal_historyGetEnr(ValueHash256 nodeId);

    [JsonRpcMethod(Description = "Delete a Node ID from the routing table")]
    ResultWrapper<bool> portal_historyDeleteEnr(ValueHash256 nodeId);

    [JsonRpcMethod(Description = "Fetch from the DHT the latest ENR associated with the given NodeId")]
    Task<ResultWrapper<string>> portal_historyLookupEnr(ValueHash256 nodeId);

    [JsonRpcMethod(Description = "Send a PING message to the designated node and wait for a PONG response.")]
    Task<ResultWrapper<PingResult>> portal_historyPing(string enr);

    [JsonRpcMethod(Description = "Send a FINDNODES request for nodes that fall within the given set of distances, to the designated peer and wait for a response.")]
    Task<ResultWrapper<string[]>> portal_historyFindNodes(string enr, ushort[] distances);

    [JsonRpcMethod(Description = "Send FINDCONTENT message to get the content with a content key.")]
    Task<ResultWrapper<FindContentResult>> portal_historyFindContent(string enr, byte[] contentKey);

    [JsonRpcMethod(Description = "Send an OFFER request with given ContentKey, to the designated peer and wait for a response.")]
    Task<ResultWrapper<byte[]>> portal_historyOffer(string enr, byte[] contentKey, byte[] contentValue);

    [JsonRpcMethod(Description = "Look up ENRs closest to the given target, that are members of the history network")]
    Task<ResultWrapper<string[]>> portal_historyRecursiveFindNodes(ValueHash256 nodeId);

    [JsonRpcMethod(Description = "Look up a target content key in the network")]
    Task<ResultWrapper<RecursiveFindContentResult>> portal_historyRecursiveFindContent(byte[] contentKey);

    [JsonRpcMethod(Description = "Look up a target content key in the network and get tracing data")]
    Task<ResultWrapper<TraceRecursiveFindContentResult>> portal_historyTraceRecursiveFindContent(byte[] contentKey);

    [JsonRpcMethod(Description = "Store history content key with content data")]
    ResultWrapper<bool> portal_historyStore(byte[] contentKey, byte[] contentValue);

    [JsonRpcMethod(Description = "Get a content from the local database")]
    ResultWrapper<byte[]> portal_historyLocalContent(byte[] contentKey);

    [JsonRpcMethod(Description = "Send the provided content item to interested peers. Clients may choose to send to some or all peers.")]
    Task<ResultWrapper<int>> portal_historyGossip(byte[] contentKey, byte[] contentValue);
}
