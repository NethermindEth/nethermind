// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Specs.ChainSpecStyle;
using System;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public class XdcPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    private INethermindApi _nethermindApi;
    public const string Xdc = "Xdc";
    public string Author => "Nethermind";
    public string Name => Xdc;
    public string Description => "Xdc support for Nethermind";
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;
    public string SealEngineType => Core.SealEngineType.XDPoS;
    public IModule Module => new XdcModule();

    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi;
        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol()
    {
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 62));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 63));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 64));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 65));
        _nethermindApi.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 100));
        return Task.CompletedTask;
    }

    // IConsensusPlugin
    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        return new XdcHotStuff(
            _nethermindApi.Context.Resolve<IBlockTree>(),
            _nethermindApi.Context.Resolve<IXdcConsensusContext>(),
            _nethermindApi.Context.Resolve<ISpecProvider>(),
            blockProducer,
            _nethermindApi.Context.Resolve<IEpochSwitchManager>(),
            _nethermindApi.Context.Resolve<ISnapshotManager>(),
            _nethermindApi.Context.Resolve<IQuorumCertificateManager>(),
            _nethermindApi.Context.Resolve<IVotesManager>(),
            _nethermindApi.Context.Resolve<ISigner>(),
            _nethermindApi.Context.Resolve<ITimeoutTimer>(),
            _nethermindApi.ProcessExit,
            _nethermindApi.Context.Resolve<ISignTransactionManager>(),
            _nethermindApi.Context.Resolve<ILogManager>()
            );
    }
    public IBlockProducer InitBlockProducer()
    {
        StartXdcBlockProducer start = _nethermindApi.Context.Resolve<StartXdcBlockProducer>();
        return start.BuildProducer();
    }
}
