// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// The fork bogota-devnet-2 runs: <see cref="FramesDevnet1"/> plus EIP-8253, EIP-4758, EIP-8298 and EIP-8151.
/// Not scheduled on any network; the devnet genesis labels it <c>bogotaTime</c>.
/// </summary>
public class BogotaDevnet2() : NamedReleaseSpec<BogotaDevnet2>(FramesDevnet1.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "BogotaDevnet2";
        spec.IsEip8253Enabled = true;
        spec.IsEip4758Enabled = true;
        spec.IsEip8298Enabled = true;
        spec.IsEip8151Enabled = true;
    }
}
