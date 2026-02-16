// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;
using System;
using System.Threading.Tasks;

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
                logger.Info("Adding eth/100 capability for XDPoS v2");
            }

            _api.ProtocolsManager?.AddSupportedCapability(new Capability(Protocol.Eth, 100));
        }

        return Task.CompletedTask;
    }
}
