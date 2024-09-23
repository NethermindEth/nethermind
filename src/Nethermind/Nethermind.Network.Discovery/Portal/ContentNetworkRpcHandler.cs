// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Network.Discovery.Portal.RpcModel;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// A large amount of the RPC methods for the portal's subprotocol are actually the same,
/// they are mostly specific to content network so this try to cover them.
/// </summary>
public class ContentNetworkRpcHandler(
    IKademlia<IEnr> kademlia,
    IRoutingTable<IEnr> kademliaRoutingTable,
    IContentDistributor contentDistributor,
    IContentNetworkProtocol contentNetworkProtocol,
    ContentLookupService contentLookupService,
    IPortalContentNetworkStore contentNetworkStore,
    ILogManager logManager,
    IEnrProvider enrProvider
)
{
    public ResultWrapper<NodeInfo> DiscV5NodeInfo()
    {
        IEnr self = enrProvider.SelfEnr;

        logManager.GetClassLogger<ContentNetworkRpcHandler>().Info($"The nodeid is {self.NodeId.ToHexString()}");

        return ResultWrapper<NodeInfo>.Success(new NodeInfo()
        {
            Enr = self.ToString()!,
            NodeId = self.NodeId.ToHexString()
        });
    }

    public ResultWrapper<RoutingTableInfoResult> RoutingTableInfo()
    {
        throw new System.NotImplementedException();
    }

    public ResultWrapper<bool> AddEnr(string enrStr)
    {
        IEnr enr = enrProvider.Decode(enrStr);

        kademlia.AddOrRefresh(enr);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string> GetEnr(ValueHash256 nodeId)
    {
        // TODO: Should this get from IRoutingTable directly?
        IEnr? enr = kademliaRoutingTable.GetByHash(nodeId);

        if (enr == null)
        {
            return ResultWrapper<string>.Fail("enr record not found");
        }
        return ResultWrapper<string>.Success(enr.ToString()!);
    }

    public ResultWrapper<bool> DeleteEnr(ValueHash256 nodeId)
    {
        bool success = kademliaRoutingTable.Remove(nodeId);
        return ResultWrapper<bool>.Success(success);
    }

    public async Task<ResultWrapper<string>> LookupEnr(ValueHash256 nodeId)
    {
        IEnr[] enrs = await kademlia.LookupNodesClosest(nodeId, default);

        if (enrs.Length < 1)
        {
            return ResultWrapper<string>.Fail("Lookup failed");
        }

        if (new ValueHash256(enrs[0].NodeId) != nodeId)
        {
            return ResultWrapper<string>.Fail("Lookup failed");
        }

        return ResultWrapper<string>.Success(enrs[0].ToString()!);
    }

    public async Task<ResultWrapper<PingResult>> Ping(string enrStr)
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

    public async Task<ResultWrapper<string[]>> FindNodes(string enrStr, ushort[] distances)
    {
        IEnr enr = enrProvider.Decode(enrStr);
        Nodes nodes = await contentNetworkProtocol.FindNodes(enr, new FindNodes()
        {
            Distances = distances
        }, default);

        IEnr[] enrs = new IEnr[nodes.Enrs.Length];
        for (var i = 0; i < nodes.Enrs.Length; i++)
        {
            enrs[i] = enrProvider.Decode(nodes.Enrs[i].Data);
        }

        string[] enrStrs = enrs.Select(it => it.ToString()!).ToArray();
        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<FindContentResult>> FindContent(string enrStr, byte[] contentKey)
    {
        CancellationToken token = new CancellationToken();

        IEnr enr = enrProvider.Decode(enrStr);
        (byte[]? payload, bool utpTransfer, IEnr[]? neighbours) = await contentLookupService.LookupContentFrom(enr, contentKey, token);

        FindContentResult result = new FindContentResult();

        if (neighbours != null)
        {
            result.Enrs = neighbours.Select(it => it.ToString()!).ToArray();
        }
        else
        {
            result.Content = payload!;
            result.UtpTransfer = utpTransfer;
        }

        return ResultWrapper<FindContentResult>.Success(result);
    }

    public async Task<ResultWrapper<byte[]>> Offer(string enrStr, byte[] contentKey, byte[] contentValue)
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        IEnr enr = enrProvider.Decode(enrStr);
        await contentDistributor.OfferAndSendContent(enr, contentKey, contentValue, cts.Token);
        return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
    }

    public async Task<ResultWrapper<string[]>> RecursiveFindNodes(ValueHash256 nodeId)
    {
        IEnr[] enrs = await kademlia.LookupNodesClosest(nodeId, default);
        string[] enrStrs = enrs.Select(it => it.ToString()!).ToArray();

        return ResultWrapper<string[]>.Success(enrStrs);
    }

    public async Task<ResultWrapper<RecursiveFindContentResult>> RecursiveFindContent(byte[] contentKey)
    {
        (byte[]? payload, bool utpTransfer) = await contentLookupService.LookupContent(contentKey, default);

        if (payload == null)
        {
            return ResultWrapper<RecursiveFindContentResult>.Fail("failed to lookup content");
        }

        var findContentResult = new RecursiveFindContentResult()
        {
            Content = payload!,
            UtpTransfer = utpTransfer
        };

        return ResultWrapper<RecursiveFindContentResult>.Success(findContentResult);
    }

    public async Task<ResultWrapper<TraceRecursiveFindContentResult>> TraceRecursiveFindContent(byte[] contentKey)
    {
        (byte[]? payload, bool utpTransfer) = await contentLookupService.LookupContent(contentKey, default);

        if (payload == null)
        {
            return ResultWrapper<TraceRecursiveFindContentResult>.Fail("failed to lookup content");
        }

        var findContentResult = new TraceRecursiveFindContentResult()
        {
            Content = payload!,
            UtpTransfer = utpTransfer
        };

        return ResultWrapper<TraceRecursiveFindContentResult>.Success(findContentResult);
    }

    public ResultWrapper<bool> Store(byte[] contentKey, byte[] contentValue)
    {
        contentNetworkStore.Store(contentKey, contentValue);

        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<byte[]> LocalContent(byte[] contentKey)
    {
        byte[]? content = contentNetworkStore.GetContent(contentKey);

        if (content == null)
        {
            return ResultWrapper<byte[]>.Fail("fail to find content");
        }

        return ResultWrapper<byte[]>.Success(content);
    }

    public async Task<ResultWrapper<int>> Gossip(byte[] contentKey, byte[] contentValue)
    {
        int distributedPeer = await contentDistributor.DistributeContent(contentKey, contentValue, default);

        return ResultWrapper<int>.Success(distributedPeer);
    }
}
