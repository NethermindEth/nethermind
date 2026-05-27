// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>
/// Net change in the storage-slot count for a single contract across one diff.
/// <see cref="SlotDelta"/> is positive when slots were added, negative when removed.
/// Consumers combine this with a per-address baseline to derive an absolute slot
/// count for downstream metrics; the walker itself stays stateless across calls.
/// </summary>
public readonly record struct SlotCountChange(ValueHash256 AddressHash, long SlotDelta);
