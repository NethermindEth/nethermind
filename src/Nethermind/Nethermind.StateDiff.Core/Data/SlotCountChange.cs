// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.StateDiff.Core.Data;

/// <summary>Net storage-slot count change for a single contract across one diff.</summary>
public readonly record struct SlotCountChange(ValueHash256 AddressHash, long SlotDelta);
