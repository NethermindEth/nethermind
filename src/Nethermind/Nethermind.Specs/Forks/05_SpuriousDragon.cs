// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class SpuriousDragon() : NamedReleaseSpec<SpuriousDragon>(TangerineWhistle.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Spurious Dragon";
        spec.MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip170;
        spec.IsEip155Enabled = true;
        spec.IsEip158Enabled = true;
        spec.IsEip160Enabled = true;
        spec.IsEip170Enabled = true;
    }
}
