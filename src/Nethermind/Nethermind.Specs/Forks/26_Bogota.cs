// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Specs.Forks;

public class Bogota() : NamedReleaseSpec<Bogota>(Amsterdam.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Bogota";
        spec.IsEip8198Enabled = true;
        spec.SlotDurationMs = Eip8198Constants.NewSlotDurationMs;
        spec.MaxBlobCount = spec.MaxBlobCount * Eip8198Constants.NewSlotDurationMs / Eip8198Constants.OldSlotDurationMs;
        spec.TargetBlobCount = spec.MaxBlobCount * 2 / 3;
        // EIP-8198 defers the fee params to the EIP-7892 "usual" derivation, where the update
        // fraction tracks the (un-truncated) target; scaling by the slot duration ratio preserves that.
        spec.BlobBaseFeeUpdateFraction = spec.BlobBaseFeeUpdateFraction * Eip8198Constants.NewSlotDurationMs / Eip8198Constants.OldSlotDurationMs;
    }
}
