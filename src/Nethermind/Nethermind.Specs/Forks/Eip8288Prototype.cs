// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-8288 (PQ signature and STARK aggregation), which extends EIP-8141. Built on
/// <see cref="Eip8141Prototype"/> to match the frame-transactions devnet base, adding
/// keyed nonces, recent roots, POST_TX frames and dependency proofs. Not scheduled on any network.
/// </summary>
public class Eip8288Prototype() : NamedReleaseSpec<Eip8288Prototype>(Eip8141Prototype.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip8288Prototype";
        spec.IsEip8141Enabled = true;
        spec.IsEip8288Enabled = true;
        spec.IsEip8250Enabled = true;
        spec.IsEip8272Enabled = true;
        spec.IsEip7906Enabled = true;
    }
}
