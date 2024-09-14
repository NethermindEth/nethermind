// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Portal;

public interface IPortalRpcModule: IRpcModule
{
    [JsonRpcMethod(IsImplemented = false, Description = "Returns meta information about history network routing table.")]
    ResultWrapper<RoutingTableInfoResult> portal_historyRoutingTableInfo();

    [JsonRpcMethod(IsImplemented = false, Description = "Write an ethereum node record to the routing table.")]
    ResultWrapper<bool> portal_historyAddEnr(string enr);

    [JsonRpcMethod(IsImplemented = false, Description = "Fetch from the local node the latest ENR associated with the given NodeId")]
    ResultWrapper<string> portal_historyGetEnr(ValueHash256 nodeId);

    [JsonRpcMethod(IsImplemented = false, Description = "Delete a Node ID from the routing table")]
    ResultWrapper<bool> portal_historyDeleteEnr(ValueHash256 nodeId);

    [JsonRpcMethod(IsImplemented = false, Description = "Fetch from the DHT the latest ENR associated with the given NodeId")]
    ResultWrapper<string> portal_historyLookupEnr(ValueHash256 nodeId);

    [JsonRpcMethod(IsImplemented = false, Description = "Send a PING message to the designated node and wait for a PONG response.")]
    ResultWrapper<PingResult> portal_historyPing(string enr);

    [JsonRpcMethod(IsImplemented = false, Description = "Send a FINDNODES request for nodes that fall within the given set of distances, to the designated peer and wait for a response.")]
    ResultWrapper<string[]> portal_historyFindNodes(string enr, ushort[] distances);

    [JsonRpcMethod(IsImplemented = false, Description = "Send FINDCONTENT message to get the content with a content key.")]
    ResultWrapper<FindContentResult> portal_historyFindContent(string enr, string contentKey);

    [JsonRpcMethod(IsImplemented = false, Description = "Send an OFFER request with given ContentKey, to the designated peer and wait for a response.")]
    ResultWrapper<string> portal_historyOffer(string enr, string contentKey, string contentValue);

    [JsonRpcMethod(IsImplemented = false, Description = "Look up ENRs closest to the given target, that are members of the history network")]
    ResultWrapper<string[]> portal_historyRecursiveFindNodes(ValueHash256 nodeId);

    [JsonRpcMethod(IsImplemented = false, Description = "Look up a target content key in the network")]
    ResultWrapper<RecursiveFindContentResult> portal_historyRecursiveFindContent(string contentKey);

    [JsonRpcMethod(IsImplemented = false, Description = "Look up a target content key in the network and get tracing data")]
    ResultWrapper<TraceRecursiveFindContentResult> portal_historyTraceRecursiveFindContent(string contentKey);

    [JsonRpcMethod(IsImplemented = false, Description = "Store history content key with content data")]
    ResultWrapper<bool> portal_historyStore(string contentKey, string contentValue);

    [JsonRpcMethod(IsImplemented = false, Description = "Get a content from the local database")]
    ResultWrapper<string> portal_historyLocalContent(string contentKey);

    [JsonRpcMethod(IsImplemented = false, Description = "Send the provided content item to interested peers. Clients may choose to send to some or all peers.")]
    ResultWrapper<int> portal_historyGossip(string contentKey, string contentValue);
}
