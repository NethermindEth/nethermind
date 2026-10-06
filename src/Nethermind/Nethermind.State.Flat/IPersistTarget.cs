// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>A block that flat persistence must land on exactly, and stop at.</summary>
/// <remarks>
/// Persistence normally only lands on CompactSize-aligned boundaries, so it steps over an arbitrary block.
/// A consumer that has to read the persisted state at one specific block — the EIP-8347 export — pulls
/// persistence onto that block through this, and nothing persists above it afterwards.
/// </remarks>
public interface IPersistTarget
{
    /// <summary>The block persistence must stop on, or null when persistence is unconstrained.</summary>
    ulong? TargetBlock { get; }

    /// <summary>How close the persisted state must be to <see cref="TargetBlock"/> before it stops
    /// batching and advances one block at a time. Ignored when there is no target.</summary>
    ulong StepDistance { get; }
}

/// <summary>The default: persistence follows its own schedule.</summary>
public sealed class UnconstrainedPersistTarget : IPersistTarget
{
    public ulong? TargetBlock => null;
    public ulong StepDistance => 0;
}
