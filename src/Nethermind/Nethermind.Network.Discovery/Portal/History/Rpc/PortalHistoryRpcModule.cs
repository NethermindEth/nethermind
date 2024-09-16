// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

extern alias BouncyCastleCryptography;
using BouncyCastleCryptography::Org.BouncyCastle.Utilities.Encoders;
using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Network.Discovery.Portal.History.Rpc.Model;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History.Rpc;

public class PortalHistoryRpcModule(
    IPortalHistoryNetwork historyNetwork,
    IEnrProvider enrProvider
): IPortalHistoryRpcModule
{
    public ResultWrapper<RoutingTableInfoResult> portal_historyRoutingTableInfo()
    {
        throw new System.NotImplementedException();
    }

    public ResultWrapper<bool> portal_historyAddEnr(string enrStr)
    {
        IEnr enr = enrProvider.Decode(enrStr);

        historyNetwork.AddEnr(enr);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string> portal_historyGetEnr(ValueHash256 nodeId)
    {
        IEnr enr = historyNetwork.GetEnr(nodeId);

        return ResultWrapper<string>.Success(enr.EncodeContent().ToHexString());
    }

    public ResultWrapper<bool> portal_historyDeleteEnr(ValueHash256 nodeId)
    {
        historyNetwork.DeleteEnr(nodeId);

        return ResultWrapper<bool>.Success(true);
    }

    public async Task<ResultWrapper<string>> portal_historyLookupEnr(ValueHash256 nodeId)
    {
        IEnr enr = await historyNetwork.LookupEnr(nodeId, default);

        return ResultWrapper<string>.Success(enr.EncodeContent().ToHexString());
    }

    public async Task<ResultWrapper<PingResult>> portal_historyPing(string enrStr)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        Pong pong = await historyNetwork.Ping(enr, default);
        UInt256 radius = new UInt256(pong.CustomPayload, true);

        return ResultWrapper<PingResult>.Success(new PingResult()
        {
            EnrSeq = pong.EnrSeq,
            DataRadius = radius
        });
    }

    public async Task<ResultWrapper<string[]>> portal_historyFindNodes(string enrStr, ushort[] distances)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        IEnr[] enrs = await historyNetwork.FindNodes(enr, distances, default);
        string[] enrStrs = enrs.Select(it => it.EncodeContent().ToHexString()).ToArray();

        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<FindContentResult>> portal_historyFindContent(string enrStr, string contentKey)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        FindContentResult findContentResult = await historyNetwork.FindContent(enr, contentKey, default);

        return ResultWrapper<FindContentResult>.Success(findContentResult);
    }

    public async Task<ResultWrapper<string>> portal_historyOffer(string enrStr, string contentKey, string contentValue)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        string response = await historyNetwork.Offer(enr, contentKey, contentValue, default);

        return ResultWrapper<string>.Success(response);
    }

    public async Task<ResultWrapper<string[]>> portal_historyRecursiveFindNodes(ValueHash256 nodeId)
    {
        IEnr[] enrs = await historyNetwork.LookupKNodes(nodeId, default);
        string[] enrStrs = enrs.Select(it => it.EncodeContent().ToHexString()).ToArray();

        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<RecursiveFindContentResult>> portal_historyRecursiveFindContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        RecursiveFindContentResult findContentResult = await historyNetwork.LookupContent(contentKey, default);

        return ResultWrapper<RecursiveFindContentResult>.Success(findContentResult);
    }

    public async Task<ResultWrapper<TraceRecursiveFindContentResult>> portal_historyTraceRecursiveFindContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        TraceRecursiveFindContentResult findContentResult = await historyNetwork.TraceLookupContent(contentKey, default);

        return ResultWrapper<TraceRecursiveFindContentResult>.Success(findContentResult);
    }

    public ResultWrapper<bool> portal_historyStore(string contentKeyStr, string contentValueStr)
    {
        // TODO: Can't it use byte array directly from rpc?
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[] contentValue = Bytes.FromHexString(contentValueStr);
        historyNetwork.Store(contentKey, contentValue);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string> portal_historyLocalContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[] content = historyNetwork.LocalContent(contentKey);

        return ResultWrapper<string>.Success(content.ToHexString());
    }

    public async Task<ResultWrapper<int>> portal_historyGossip(string contentKeyStr, string contentValueStr)
    {
        // TODO: Can't it use byte array directly from rpc?
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[] contentValue = Bytes.FromHexString(contentValueStr);
        int resp = await historyNetwork.Gossip(contentKey, contentValue, default);

        return ResultWrapper<int>.Success(resp);
    }
}
