// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.LightClient;

internal sealed record VerifiedHead(ulong Slot, ulong Number, Hash256 BlockHash, Hash256 StateRoot)
{
    internal bool IsFresh(ulong currentSlot, ulong maximumAgeSlots) =>
        currentSlot >= Slot && currentSlot - Slot <= maximumAgeSlots;
}
