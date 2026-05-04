// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

/// <summary>
/// Historical state availability of a world-state implementation, reported through
/// <c>eth_capabilities</c>. <see cref="RetentionWindowBlocks"/> is the rolling-window
/// retention; null means no rolling window (archive or non-linear retention). The absolute
/// floor of historical state lives separately in <c>IBlockTree.OldestStateBlock</c>.
/// </summary>
public readonly record struct StateAvailability(long? RetentionWindowBlocks);
