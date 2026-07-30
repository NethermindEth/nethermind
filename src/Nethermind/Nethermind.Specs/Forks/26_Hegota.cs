// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Hegotá network upgrade (EIP-8081), following Amsterdam (Glamsterdam).
/// </summary>
/// <remarks>
/// The EIP set is not final yet — EIPs are added here as they get scheduled for
/// inclusion and implemented. See https://eips.ethereum.org/EIPS/eip-8081.
/// </remarks>
public class Hegota() : NamedReleaseSpec<Hegota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec) => spec.Name = "Hegota";
}
