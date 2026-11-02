// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Stateless.Execution;

internal sealed partial class StatelessSpecProvider
{
    // Mainnet skips the known-providers index, which constructs every built-in chain's provider.
    private static IForkAwareSpecProvider? GetKnownProvider(ulong chainId) =>
        chainId == BlockchainIds.Mainnet
            ? MainnetSpecProvider.Instance
            : ChainSpecBasedSpecProvider.KnownProvidersByChainId.GetValueOrDefault(chainId);
}
