// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Portal.History.Rpc.Model;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History;

public class PortalHistoryNetwork: IPortalContentNetwork.Store, IPortalHistoryNetwork
{
    private readonly IPortalContentNetwork _contentNetwork;
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;

    public PortalHistoryNetwork(
        IPortalContentNetworkFactory portalContentNetworkFactory,
        IBlockTree blockTree,
        ILogManager logManager,
        byte[] protocolId,
        IEnr[] bootNodes
    ) {
        _contentNetwork = portalContentNetworkFactory.Create(new ContentNetworkConfig()
        {
            ProtocolId = protocolId,
            BootNodes = bootNodes
        }, this);

        _blockTree = blockTree;
        _logger = logManager.GetClassLogger<PortalHistoryNetwork>();
    }

    public byte[]? GetContent(byte[] contentKey)
    {
        ContentKey key = SlowSSZ.Deserialize<ContentKey>(contentKey);

        if (key.HeaderKey != null)
        {
            BlockHeader? header = _blockTree.FindHeader(key.HeaderKey!);
            if (header == null) return null;

            return _encoderDecoder.EncodeHeader(header!);
        }

        if (key.BodyKey != null)
        {
            Block? block = _blockTree.FindBlock(key.BodyKey!);
            if (block == null) return null;

            return _encoderDecoder.EncodeBlockBody(block.Body!);
        }

        throw new Exception($"unsupported content {contentKey}");
    }

    public bool ShouldAcceptOffer(byte[] offerContentKey)
    {
        // Note: Just testing
        return true;
    }

    public void AddEnr(IEnr enr)
    {
        throw new NotImplementedException();
    }

    public IEnr GetEnr(ValueHash256 nodeId)
    {
        throw new NotImplementedException();
    }

    public void DeleteEnr(ValueHash256 nodeId)
    {
        throw new NotImplementedException();
    }

    public Task<IEnr> LookupEnr(ValueHash256 nodeId, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<Pong> Ping(IEnr enr, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<IEnr[]> FindNodes(IEnr enr, ushort[] distances, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<FindContentResult> FindContent(IEnr enr, string contentKey, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<string> Offer(IEnr enr, string contentKey, string contentValue, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<IEnr[]> LookupKNodes(ValueHash256 nodeId, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<RecursiveFindContentResult> LookupContent(byte[] contentKey, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public Task<TraceRecursiveFindContentResult> TraceLookupContent(byte[] contentKey, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public void Store(byte[] contentKey, byte[] content)
    {
        // Note: Just testing
        _logger.Info($"Got content {contentKey.ToHexString()} of size {content.Length} from portal network");
    }

    public byte[] LocalContent(byte[] contentKey)
    {
        throw new NotImplementedException();
    }

    public Task<int> Gossip(byte[] contentKey, byte[] contentValue, CancellationToken token)
    {
        throw new NotImplementedException();
    }

    private async Task<BlockHeader?> LookupBlockHeader(ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up header {hash}");

        byte[]? asBytes = await _contentNetwork.LookupContent(SlowSSZ.Serialize(new ContentKey()
        {
            HeaderKey = hash
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeHeader(asBytes!);
    }

    private async Task<BlockBody?> LookupBlockBody(ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up body {hash}");

        byte[]? asBytes = await _contentNetwork.LookupContent(SlowSSZ.Serialize(new ContentKey()
        {
            BodyKey = hash
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeBody(asBytes!);
    }

    private async Task<BlockBody?> LookupBlockBodyFrom(IEnr enr, ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up body {hash}");

        byte[]? asBytes = await _contentNetwork.LookupContentFrom(enr, SlowSSZ.Serialize(new ContentKey()
        {
            BodyKey = hash
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeBody(asBytes!);
    }

    public async Task Run(CancellationToken token)
    {
        _logger.Info("Running portal history network. Bootstrapping.");

        // You can skip bootstrap for testing, but the lookup time is going to be less realistic.
        await _contentNetwork.Bootstrap(token);

        // EnrEntryRegistry registry = new EnrEntryRegistry();
        // EnrFactory enrFactory = new(registry);
        // IdentityVerifierV4 identityVerifier = new();
        // var enr = enrFactory.CreateFromString("enr:-IS4QIvH4sUnNXGBWyR2M8GUb9B0haxCbqYZgC_9HYgvR890B8t3u44EeJpRA7czZBgDVzAovXEwx_F56YLU9ZoIhRAhgmlkgnY0gmlwhMIhK1qJc2VjcDI1NmsxoQKKqT_1W3phl5Ial-DBViE0MIZbwAHdRyrpZWKe0ttv4oN1ZHCCI4w", identityVerifier);
        // _logger.Info("-------------- looking up ----------------------");
        // BlockBody? body = await LookupBlockBody(new ValueHash256("0xead3ee2e6370d110e02840d700097d844ca4d1f62697194564f687985dfe2c1a"), token);
        // _logger.Info($"Lookup body got {body}");

        // await _contentNetwork.Run(token);
    }
}
