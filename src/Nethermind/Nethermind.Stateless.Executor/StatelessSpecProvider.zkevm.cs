// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Stateless.Execution;

// The guest registers no plugin providers, so it builds only the input chain's provider, not every known chain's.
// Mirrors ChainSpecBasedSpecProvider's built-in providers; StatelessSpecProviderTests fails when they diverge.
internal sealed partial class StatelessSpecProvider
{
    private static IForkAwareSpecProvider? GetKnownProvider(ulong chainId) => chainId switch
    {
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Gnosis => GnosisSpecProvider.Instance,
        BlockchainIds.Chiado => ChiadoSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Morden => MordenSpecProvider.Instance,
        _ => null
    };
}
