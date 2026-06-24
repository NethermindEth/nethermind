// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Bogota";
        spec.IsEip8198Enabled = true;
        spec.SlotDurationMs = Eip8198Constants.NewSlotDurationMs;
        spec.MaxBlobCount = spec.MaxBlobCount * Eip8198Constants.NewSlotDurationMs / Eip8198Constants.OldSlotDurationMs;
        spec.TargetBlobCount = spec.MaxBlobCount * 2 / 3;
    }
}
