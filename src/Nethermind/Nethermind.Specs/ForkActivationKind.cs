// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs;

/// <summary>
/// Disambiguates a <see cref="ForkSchedule"/> entry's key as a block number or a timestamp.
/// </summary>
/// <remarks>
/// Used as the second argument to <see cref="ForkSchedule"/>'s indexer so the API does not
/// depend on signed (block) vs unsigned (timestamp) type dispatch — keeping it valid once
/// block numbers migrate to <see cref="ulong"/>.
/// </remarks>
public enum ForkActivationKind
{
    Block,
    Timestamp,
}
