// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Historical state availability of a world-state implementation, reported through
/// <c>eth_capabilities</c>. <see cref="RetentionWindowBlocks"/> is null when archive
/// (use <see cref="Archive"/>) or when retention is non-linear (e.g. full pruning).
/// </summary>
public readonly record struct StateAvailability(
    bool Archive,
    long? RetentionWindowBlocks,
    bool StateProofsSupported);
