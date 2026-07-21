// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Wallet;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies]
    public class SetupKeyStore(
        INetworkConfig networkConfig,
        AccountUnlocker accountUnlocker,
        [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            accountUnlocker.UnlockAccounts();

            networkConfig.Bootnodes = [.. networkConfig.Bootnodes.Where(bn => bn.NodeId != nodeKey.PublicKey)];

            return Task.CompletedTask;
        }
    }
}
