// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

/// <summary>
/// Prototype fork for EIP-4758 SENDALL on top of Amsterdam. Not scheduled on any network; exists so a
/// devnet can activate it on its own, through <c>eip4758TransitionTimestamp</c>, this label, or the
/// Geth-genesis <c>eip4758PrototypeTime</c>, and so the execution-specs EIP-4758 fixtures, whose
/// <c>Bogota</c> is Amsterdam plus EIP-4758, have a fork to alias to (<c>--forkAlias Bogota=Eip4758Prototype</c>).
/// </summary>
public class Eip4758Prototype() : NamedReleaseSpec<Eip4758Prototype>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Eip4758Prototype";
        spec.IsEip4758Enabled = true;
    }
}
