// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Devnet fork enabling EIP-8141 frame transactions on top of Osaka. Matches the frame
/// transactions devnet layout, where the genesis generator schedules the frame-tx opcodes via
/// <c>bogotaTime</c> over an Osaka-from-genesis network. Not scheduled on any public network.
/// </summary>
public class Bogota() : NamedReleaseSpec<Bogota>(Osaka.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "bogota";
        spec.IsEip8141Enabled = true;
    }
}
