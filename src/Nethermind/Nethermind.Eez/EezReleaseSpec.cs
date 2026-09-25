// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Eez;

/// <summary>An EEZ genesis that names no EIP-6110 deposit contract gets the mainnet one.</summary>
/// <remarks>A geth-style genesis without <c>depositContractAddress</c> loads as <see cref="Address.Zero"/>.</remarks>
public sealed class EezReleaseSpec : ReleaseSpecDecorator
{
    private readonly Address? _depositContractAddress;

    public EezReleaseSpec(IReleaseSpec spec) : base(spec)
    {
        Address? configured = spec.DepositContractAddress;
        _depositContractAddress = spec.IsEip6110Enabled && (configured is null || configured == Address.Zero)
            ? Eip6110Constants.MainnetDepositContractAddress
            : configured;
    }

    public override Address? DepositContractAddress => _depositContractAddress;
}
