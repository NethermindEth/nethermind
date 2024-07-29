// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
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
        _logger.Info($"Looking up header {hash}");

        byte[]? asBytes = await _contentNetwork.LookupContent(SlowSSZ.Serialize(new ContentKey()
        {
            BodyKey = hash
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeBody(asBytes!);
    }

    public async Task Run(CancellationToken token)
    {
        _logger.Info("Running portal history network. Bootstrapping.");
        await _contentNetwork.Bootstrap(token);

        BlockBody? body = await LookupBlockBody(new ValueHash256("0x8547876c30570eac4807f8e93ff2d9b5ebb5561ba855a305293fec08da650d65"), token);
        _logger.Info($"Lookup body got {body}");

        // await _contentNetwork.Run(token);
    }
}
