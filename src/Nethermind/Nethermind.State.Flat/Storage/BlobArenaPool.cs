// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Identifies which of the two persisted-snapshot pool tiers a
/// <see cref="BlobArenaManager"/> serves. Persisted alongside each blob arena
/// catalog entry so on restart the right manager rehydrates its slice.
/// </summary>
public enum BlobArenaPool : byte
{
    Small = 0,
    Large = 1,
}
