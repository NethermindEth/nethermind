// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;
using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;

namespace Nethermind.Xdc;

public class XdcPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Xdc = "Xdc";
    public string Author => "Nethermind";
    public string Name => Xdc;
    public string Description => "Xdc support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => Core.SealEngineType.XDPoS;
    public IModule Module => new XdcModule();

    private INethermindApi? _api;
    private ILogger? _logger;

    // IConsensusPlugin
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer _)
    {
        throw new NotSupportedException();
    }
    public IBlockProducer InitBlockProducer()
    {
        throw new NotSupportedException();
    }

    // INethermindPlugin
    public Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _logger = _api.LogManager.GetClassLogger();

        if (Enabled)
        {
            // Initialize persistent state root cache for XDC
            var initConfig = nethermindApi.Config<IInitConfig>();
            var dataDir = initConfig?.BaseDbPath ?? "xdc";
            XdcStateRootCache.SetPersistPath(dataDir);
            if (_logger is { IsInfo: true } cacheLogger)
                cacheLogger.Info($"XdcStateRootCache persistence initialized at {dataDir}");

            // Register XDC header/block decoders globally so all P2P message serializers
            // (BlockHeadersMessage, NewBlockMessage, etc.) decode XDC block headers
            // with the extra Validator/Validators/Penalties fields
            var xdcHeaderDecoder = new XdcHeaderDecoder();
            Rlp.RegisterDecoder(typeof(BlockHeader), xdcHeaderDecoder);
            Rlp.RegisterDecoder(typeof(Block), new BlockDecoder(xdcHeaderDecoder));
            if (_logger is { IsInfo: true } logger)
                logger.Info("Registered XdcHeaderDecoder as global BlockHeader/Block decoder");
        }

        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        if (_api is null)
        {
            throw new InvalidOperationException("XdcPlugin.Init() must be called before InitNetworkProtocol()");
        }

        if (Enabled)
        {
            if (_logger is { } logger && logger.IsInfo)
            {
                logger.Info("Adding eth/62, eth/63, eth/100 capabilities for XDC mainnet compatibility");
            }

            // Add eth/62, eth/63, eth/100 for XDC mainnet compatibility
            // XDC uses eth/62-style handshake (no ForkID) and eth/63-style messages
            _api.ProtocolsManager?.AddSupportedCapability(new Capability(Protocol.Eth, 62));
            _api.ProtocolsManager?.AddSupportedCapability(new Capability(Protocol.Eth, 63));
            _api.ProtocolsManager?.AddSupportedCapability(new Capability(Protocol.Eth, 100));

            // Register custom eth/100 protocol factory
            var customFactory = _api.Context.ResolveOptional<ICustomEthProtocolFactory>();
            if (customFactory is not null)
            {
                _api.ProtocolsManager?.SetCustomEthProtocolFactory(customFactory);
                if (_logger is { } logger2 && logger2.IsDebug)
                {
                    logger2.Debug("Registered custom eth/100 protocol factory");
                }
            }
            else if (_logger is { } logger3 && logger3.IsWarn)
            {
                logger3.Warn("Custom eth/100 protocol factory not found in container");
            }
        }

        return Task.CompletedTask;
    }
}
