// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Specs.Forks;

public class Byzantium() : NamedReleaseSpec<Byzantium>(SpuriousDragon.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Byzantium";
        spec.BlockReward = new UInt256(3000000000000000000ul);
        spec.DifficultyBombDelay = 3000000L;
        spec.IsEip100Enabled = true;
        spec.IsEip140Enabled = true;
        spec.IsEip196Enabled = true;
        spec.IsEip197Enabled = true;
        spec.IsEip198Enabled = true;
        spec.IsEip211Enabled = true;
        spec.IsEip214Enabled = true;
        spec.IsEip649Enabled = true;
        spec.IsEip658Enabled = true;
    }
}
