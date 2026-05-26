// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Bogota";
        spec.IsEip7805Enabled = true;
    }
}
