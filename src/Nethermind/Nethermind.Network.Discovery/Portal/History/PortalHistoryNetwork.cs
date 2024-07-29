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
        // await _contentNetwork.Bootstrap(token);

        _logger.Info("-------------- looking up ----------------------");
        BlockBody? body = await LookupBlockBody(new ValueHash256("0x40c91b6ef0ac682432c746ac0bbabc27c2864c5d9fb73ca663ec7acfe8033810"), token);
        _logger.Info($"Lookup body got {body}");

        // await _contentNetwork.Run(token);
    }
}
