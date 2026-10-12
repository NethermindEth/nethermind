// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State;

/// <summary>Parent header provider for contexts where parent lookup is unavailable.</summary>
public sealed class UnavailableStateHeaderProvider : IStateHeaderProvider
{
    public static UnavailableStateHeaderProvider Instance { get; } = new();

    private UnavailableStateHeaderProvider() { }

    /// <inheritdoc />
    public BlockHeader? FindParentHeader(BlockHeader target) => null;

    /// <inheritdoc />
    public ulong FinalizedBlockNumber => 0;

    /// <inheritdoc />
    public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
}
