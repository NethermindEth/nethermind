// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;

namespace Nethermind.Network;

public class EnodeProvider(
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    INetworkConfig networkConfig,
    ILogManager logManager)
{
    private IEnode? _enode;

    public IEnode Enode => _enode ??= Build();

    private IEnode Build()
    {
        IPAddress ipAddress = networkConfig.ExternalIp is not null
            ? IPAddress.Parse(networkConfig.ExternalIp)
            : IPAddress.Loopback;
        Enode enode = new(nodeKey.PublicKey, ipAddress, networkConfig.P2PPort);
        logManager.SetGlobalVariable("enode", enode.ToString());
        return enode;
    }
}
