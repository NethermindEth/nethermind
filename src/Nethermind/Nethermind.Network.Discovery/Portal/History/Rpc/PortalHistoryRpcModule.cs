// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

extern alias BouncyCastleCryptography;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Network.Discovery.Portal.RpcModel;

namespace Nethermind.Network.Discovery.Portal.History.Rpc;

public class PortalHistoryRpcModule(
    ContentNetworkRpcHandler contentNetworkRpcHandler
): IPortalHistoryRpcModule
{
    public ResultWrapper<NodeInfo> discv5_nodeInfo()
    {
        return contentNetworkRpcHandler.DiscV5NodeInfo();
    }

    public ResultWrapper<RoutingTableInfoResult> portal_historyRoutingTableInfo()
    {
        return contentNetworkRpcHandler.RoutingTableInfo();
    }

    public ResultWrapper<bool> portal_historyAddEnr(string enrStr)
    {
        return contentNetworkRpcHandler.AddEnr(enrStr);
    }

    public ResultWrapper<string> portal_historyGetEnr(ValueHash256 nodeId)
    {
        return contentNetworkRpcHandler.GetEnr(nodeId);
    }

    public ResultWrapper<bool> portal_historyDeleteEnr(ValueHash256 nodeId)
    {
        return contentNetworkRpcHandler.DeleteEnr(nodeId);
    }

    public Task<ResultWrapper<string>> portal_historyLookupEnr(ValueHash256 nodeId)
    {
        return contentNetworkRpcHandler.LookupEnr(nodeId);
    }

    public Task<ResultWrapper<PingResult>> portal_historyPing(string enrStr)
    {
        return contentNetworkRpcHandler.Ping(enrStr);
    }

    public Task<ResultWrapper<string[]>> portal_historyFindNodes(string enrStr, ushort[] distances)
    {
        return contentNetworkRpcHandler.FindNodes(enrStr, distances);
    }

    public Task<ResultWrapper<FindContentResult>> portal_historyFindContent(string enrStr, byte[] contentKey)
    {
        return contentNetworkRpcHandler.FindContent(enrStr, contentKey);
    }

    public Task<ResultWrapper<byte[]>> portal_historyOffer(string enrStr, byte[] contentKey, byte[] contentValue)
    {
        return contentNetworkRpcHandler.Offer(enrStr, contentKey, contentValue);
    }

    public Task<ResultWrapper<string[]>> portal_historyRecursiveFindNodes(ValueHash256 nodeId)
    {
        return contentNetworkRpcHandler.RecursiveFindNodes(nodeId);
    }

    public Task<ResultWrapper<RecursiveFindContentResult>> portal_historyRecursiveFindContent(byte[] contentKey)
    {
        return contentNetworkRpcHandler.RecursiveFindContent(contentKey);
    }

    public Task<ResultWrapper<TraceRecursiveFindContentResult>> portal_historyTraceRecursiveFindContent(byte[] contentKey)
    {
        return contentNetworkRpcHandler.TraceRecursiveFindContent(contentKey);
    }

    public ResultWrapper<bool> portal_historyStore(byte[] contentKey, byte[] contentValue)
    {
        return contentNetworkRpcHandler.Store(contentKey, contentValue);
    }

    public ResultWrapper<byte[]> portal_historyLocalContent(byte[] contentKey)
    {
        return contentNetworkRpcHandler.LocalContent(contentKey);
    }

    public Task<ResultWrapper<int>> portal_historyGossip(byte[] contentKey, byte[] contentValue)
    {
        return contentNetworkRpcHandler.Gossip(contentKey, contentValue);
    }
}
