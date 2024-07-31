// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Portal.History;

public class PortalHistoryNetwork
{
    private readonly IPortalContentNetwork _contentNetwork;
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly ILogger _logger;

    public PortalHistoryNetwork(ILanternAdapter lanternAdapter, ILogManager logManager, byte[] protocolId, IEnr[] bootNodes)
    {
        _contentNetwork = lanternAdapter.RegisterContentNetwork(protocolId, new NoopStore());
        foreach (IEnr bootNode in bootNodes)
        {
            _contentNetwork.AddSeed(bootNode);
        }

        _logger = logManager.GetClassLogger<PortalHistoryNetwork>();
    }

    private class NoopStore : IPortalContentNetwork.Store
    {
        public byte[]? GetContent(byte[] contentKey)
        {
            return null;
        }
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
        // await _contentNetwork.Bootstrap(token);


        // EnrEntryRegistry registry = new EnrEntryRegistry();
        // EnrFactory enrFactory = new(registry);
        // IdentityVerifierV4 identityVerifier = new();
        // var enr = enrFactory.CreateFromString("enr:-IS4QIvH4sUnNXGBWyR2M8GUb9B0haxCbqYZgC_9HYgvR890B8t3u44EeJpRA7czZBgDVzAovXEwx_F56YLU9ZoIhRAhgmlkgnY0gmlwhMIhK1qJc2VjcDI1NmsxoQKKqT_1W3phl5Ial-DBViE0MIZbwAHdRyrpZWKe0ttv4oN1ZHCCI4w", identityVerifier);
        _logger.Info("-------------- looking up ----------------------");
        BlockBody? body = await LookupBlockBody(new ValueHash256("0xead3ee2e6370d110e02840d700097d844ca4d1f62697194564f687985dfe2c1a"), token);
        _logger.Info($"Lookup body got {body}");

        // await _contentNetwork.Run(token);
    }
}
