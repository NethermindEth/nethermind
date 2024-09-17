// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

extern alias BouncyCastleCryptography;
using BouncyCastleCryptography::Org.BouncyCastle.Utilities.Encoders;
using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.History.Rpc.Model;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History.Rpc;

public class PortalHistoryRpcModule(
    IKademlia<IEnr, byte[], byte[]> kademlia,
    IRoutingTable<IEnr> kademliaRoutingTable,
    IContentDistributor contentDistributor,
    IContentNetworkProtocol contentNetworkProtocol,
    ContentLookupService contentLookupService,
    IPortalContentNetwork.Store contentNetworkStore,
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

        kademlia.AddOrRefresh(enr);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string> portal_historyGetEnr(ValueHash256 nodeId)
    {
        // TODO: Should this get from IRoutingTable directly?
        IEnr? enr = kademliaRoutingTable.GetByHash(nodeId);

        if (enr == null)
        {
            return ResultWrapper<string>.Fail("enr record not found");
        }

        return ResultWrapper<string>.Success(enr.EncodeContent().ToHexString());
    }

    public ResultWrapper<bool> portal_historyDeleteEnr(ValueHash256 nodeId)
    {
        kademliaRoutingTable.Remove(nodeId);

        return ResultWrapper<bool>.Success(true);
    }

    public async Task<ResultWrapper<string>> portal_historyLookupEnr(ValueHash256 nodeId)
    {
        IEnr[] enrs = await kademlia.LookupNodesClosest(nodeId, default, 1);

        return ResultWrapper<string>.Success(enrs[0].EncodeContent().ToHexString());
    }

    public async Task<ResultWrapper<PingResult>> portal_historyPing(string enrStr)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        Pong pong = await contentNetworkProtocol.Ping(enr, new Ping(), default);
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
        Nodes nodes = await contentNetworkProtocol.FindNodes(enr, new FindNodes()
        {
            Distances = distances
        }, default);

        IEnr[] enrs = new IEnr[nodes.Enrs.Length];
        for (var i = 0; i < nodes.Enrs.Length; i++)
        {
            enrs[i] = enrProvider.Decode(nodes.Enrs[i]);
        }

        string[] enrStrs = enrs.Select(it => it.EncodeContent().ToHexString()).ToArray();
        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<FindContentResult>> portal_historyFindContent(string enrStr, string contentKeyStr)
    {
        CancellationToken token = new CancellationToken();

        IEnr enr = enrProvider.Decode(enrStr);
        (byte[]? payload, bool utpTransfer, IEnr[]? neighbours) = await contentLookupService.LookupContentFrom(enr, Bytes.FromHexString(contentKeyStr), token);

        FindContentResult result = new FindContentResult();

        if (neighbours != null)
        {
            result.Enrs = neighbours.Select(it => it.EncodeContent().ToHexString()).ToArray();
        }
        else
        {
            result.Content = payload!.ToHexString();
            result.UtpTransfer = utpTransfer;
        }

        return ResultWrapper<FindContentResult>.Success(result);
    }

    public async Task<ResultWrapper<string>> portal_historyOffer(string enrStr, string contentKeyStr, string contentValueStr)
    {
        IEnr enr = enrProvider.Decode(enrStr);

        Accept accept = await contentNetworkProtocol.Offer(enr, new Offer()
        {
            ContentKeys = [Bytes.FromHexString(contentKeyStr)]
        }, default);
        // TODO: Do we also send it?

        return ResultWrapper<string>.Success(accept.AcceptedBits.ToBitString());
    }

    public async Task<ResultWrapper<string[]>> portal_historyRecursiveFindNodes(ValueHash256 nodeId)
    {
        IEnr[] enrs = await kademlia.LookupNodesClosest(nodeId, default);
        string[] enrStrs = enrs.Select(it => it.EncodeContent().ToHexString()).ToArray();

        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<RecursiveFindContentResult>> portal_historyRecursiveFindContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        (byte[]? payload, bool utpTransfer) = await contentLookupService.LookupContent(contentKey, default);

        if (payload == null)
        {
            return ResultWrapper<RecursiveFindContentResult>.Fail("failed to lookup content");
        }

        var findContentResult = new RecursiveFindContentResult()
        {
            Content = payload!.ToHexString(),
            UtpTransfer = utpTransfer
        };

        return ResultWrapper<RecursiveFindContentResult>.Success(findContentResult);
    }

    public async Task<ResultWrapper<TraceRecursiveFindContentResult>> portal_historyTraceRecursiveFindContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        (byte[]? payload, bool utpTransfer) = await contentLookupService.LookupContent(contentKey, default);

        if (payload == null)
        {
            return ResultWrapper<TraceRecursiveFindContentResult>.Fail("failed to lookup content");
        }

        var findContentResult = new TraceRecursiveFindContentResult()
        {
            Content = payload!.ToHexString(),
            UtpTransfer = utpTransfer
        };

        return ResultWrapper<TraceRecursiveFindContentResult>.Success(findContentResult);
    }

    public ResultWrapper<bool> portal_historyStore(string contentKeyStr, string contentValueStr)
    {
        // TODO: Can't it use byte array directly from rpc?
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[] contentValue = Bytes.FromHexString(contentValueStr);

        contentNetworkStore.Store(contentKey, contentValue);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string> portal_historyLocalContent(string contentKeyStr)
    {
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[]? content = contentNetworkStore.GetContent(contentKey);

        if (content == null)
        {
            return ResultWrapper<string>.Fail("fail to find content");
        }

        return ResultWrapper<string>.Success(content.ToHexString());
    }

    public async Task<ResultWrapper<int>> portal_historyGossip(string contentKeyStr, string contentValueStr)
    {
        // TODO: Can't it use byte array directly from rpc?
        byte[] contentKey = Bytes.FromHexString(contentKeyStr);
        byte[] contentValue = Bytes.FromHexString(contentValueStr);

        int distributedPeer = await contentDistributor.DistributeContent(contentKey, contentValue, default);

        return ResultWrapper<int>.Success(distributedPeer);
    }
}
