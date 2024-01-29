// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(ResolveIps))]
public class SetupKeyStore : IStep
{
    private readonly ProtectedPrivateKey _nodeKey;
    private readonly ChainSpec _chainSpec;
    private readonly INetworkConfig _networkConfig;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly EnodeContainer _enodeContainer;
    private readonly ILogManager _logManager;

    public SetupKeyStore(
        [KeyFilter(ComponentKey.NodeKey)] ProtectedPrivateKey nodeKey,
        ChainSpec chainSpec,
        INetworkConfig networkConfig,
        IDiscoveryConfig discoveryConfig,
        EnodeContainer enodeContainer,
        ILogManager logManager
    )
    {
        _nodeKey = nodeKey;
        _chainSpec = chainSpec;
        _networkConfig = networkConfig;
        _discoveryConfig = discoveryConfig;
        _logManager = logManager;
        _enodeContainer = enodeContainer;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        SetEnode();
        FilterBootNodes();
        UpdateDiscoveryConfig();

        return Task.CompletedTask;
    }

    private void SetEnode()
    {
        IPAddress ipAddress = _networkConfig.ExternalIp is not null ? IPAddress.Parse(_networkConfig.ExternalIp) : IPAddress.Loopback;
        IEnode enode = _enodeContainer.Enode = new Enode(_nodeKey!.PublicKey, ipAddress, _networkConfig.P2PPort);
        _logManager.SetGlobalVariable("enode", enode.ToString());
    }

    private void FilterBootNodes()
    {
        _chainSpec.Bootnodes = _chainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_nodeKey.PublicKey) ?? false).ToArray() ?? Array.Empty<NetworkNode>();
    }

    private void UpdateDiscoveryConfig()
    {
        if (_discoveryConfig.Bootnodes != string.Empty)
        {
            if (_chainSpec.Bootnodes.Length != 0)
            {
                _discoveryConfig.Bootnodes += "," + string.Join(",", _chainSpec.Bootnodes.Select(bn => bn.ToString()));
            }
        }
        else
        {
            _discoveryConfig.Bootnodes = string.Join(",", _chainSpec.Bootnodes.Select(bn => bn.ToString()));
        }
    }
}
