// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Nethermind.State.Pbt.Test;

/// <summary>A Prague chain that activates EIP-8347 at timestamp <see cref="Activation"/>.</summary>
internal static class MigrationTestSpecs
{
    public const ulong Activation = 48;

    public static ISpecProvider Create() => new CustomSpecProvider(
        ((ForkActivation)0, Prague.Instance),
        (ForkActivation.TimestampOnly(Activation), new OverridableReleaseSpec(Prague.Instance) { IsEip8347Enabled = true }));
}
