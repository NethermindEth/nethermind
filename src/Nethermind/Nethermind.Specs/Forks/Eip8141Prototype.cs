// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8141 frame transactions. Not scheduled on any network; exists to gate
/// the frame transaction prototype behind <c>IsEip8141Enabled</c>. Excluded from the Geth genesis
/// fork mapping because it is not a real fork name.
/// </summary>
public class Eip8141Prototype() : NamedReleaseSpec<Eip8141Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8141Prototype";
        spec.IsEip8141Enabled = true;
    }
}
