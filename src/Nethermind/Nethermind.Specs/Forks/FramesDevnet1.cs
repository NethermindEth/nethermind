// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// The fork frames-devnet-1 runs: EIP-8141 frame transactions with EIP-8250, EIP-8272 and EIP-7906 on
/// top of Amsterdam. Not scheduled on any network; names the combination the devnet's test fixtures
/// call <c>Bogota</c>.
/// </summary>
public class FramesDevnet1() : NamedReleaseSpec<FramesDevnet1>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "FramesDevnet1";
        spec.IsEip8250Enabled = true;
        spec.IsEip8272Enabled = true;
        spec.IsEip7906Enabled = true;
    }
}
