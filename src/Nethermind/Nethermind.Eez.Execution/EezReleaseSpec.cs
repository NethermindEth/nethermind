// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution;

/// <summary>
/// A fork as an EEZ chain runs it: a genesis that names no EIP-6110 deposit contract gets the mainnet one, and
/// RollupX's EIP-1559 denominator replaces the fork's.
/// </summary>
/// <remarks>A geth-style genesis without <c>depositContractAddress</c> loads as <see cref="Address.Zero"/>.</remarks>
public sealed class EezReleaseSpec(IReleaseSpec spec, EezChainRules rules) : ReleaseSpecDecorator(spec)
{
    public static bool Changes(IReleaseSpec spec, EezChainRules rules) =>
        NeedsDepositContractDefault(spec) || rules.BaseFeeMaxChangeDenominator is not null || rules.FeeCollector is not null;

    public static bool NeedsDepositContractDefault(IReleaseSpec spec) =>
        spec.IsEip6110Enabled && (spec.DepositContractAddress is null || spec.DepositContractAddress == Address.Zero);

    public override Address? DepositContractAddress =>
        base.IsEip6110Enabled && (base.DepositContractAddress is null || base.DepositContractAddress == Address.Zero)
            ? Eip6110Constants.MainnetDepositContractAddress
            : base.DepositContractAddress;

    public override Address? FeeCollector => rules.FeeCollector ?? base.FeeCollector;

    public override UInt256 BaseFeeMaxChangeDenominator => rules.BaseFeeMaxChangeDenominator ?? base.BaseFeeMaxChangeDenominator;
}
