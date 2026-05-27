// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.State;

/// <summary>
/// Thrown when an operation requires a world-state scope at a specific block, but that state has
/// been pruned or is otherwise unavailable. RPC entry points translate this into a
/// <c>ResourceUnavailable</c> error so callers can distinguish "state gone" from execution errors.
/// </summary>
public sealed class StateUnavailableException(BlockHeader? header)
    : InvalidOperationException(BuildMessage(header))
{
    public BlockHeader? Header { get; } = header;

    private static string BuildMessage(BlockHeader? header) =>
        $"No state available for block {header?.ToString(BlockHeader.Format.FullHashAndNumber) ?? "(null)"}";
}
