// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Specs.Forks;

public class Constantinople() : NamedReleaseSpec<Constantinople>(Byzantium.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Constantinople";
        spec.BlockReward = new UInt256(2000000000000000000ul);
        spec.DifficultyBombDelay = 5000000L;
        spec.IsEip145Enabled = true;
        spec.IsEip1014Enabled = true;
        spec.IsEip1052Enabled = true;
        spec.IsEip1283Enabled = true;
        spec.IsEip1234Enabled = true;
    }
}
