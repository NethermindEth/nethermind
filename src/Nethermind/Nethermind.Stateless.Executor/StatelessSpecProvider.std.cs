// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Stateless.Execution;

internal sealed partial class StatelessSpecProvider
{
    private static IForkAwareSpecProvider? GetKnownProvider(ulong chainId) =>
        ChainSpecBasedSpecProvider.KnownProvidersByChainId.GetValueOrDefault(chainId);
}
