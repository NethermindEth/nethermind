// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Eez.Execution;

/// <summary>An EEZ genesis that names no EIP-6110 deposit contract gets the mainnet one.</summary>
/// <remarks>A geth-style genesis without <c>depositContractAddress</c> loads as <see cref="Address.Zero"/>.</remarks>
public sealed class EezReleaseSpec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public static bool NeedsDepositContractDefault(IReleaseSpec spec) =>
        spec.IsEip6110Enabled && (spec.DepositContractAddress is null || spec.DepositContractAddress == Address.Zero);

    public override Address DepositContractAddress => Eip6110Constants.MainnetDepositContractAddress;
}
