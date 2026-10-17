// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>A rollup's state an execution entry starts from, as the L1 rolling-hash seed commits to it.</summary>
public readonly record struct StateCommitment(ulong RollupId, ValueHash256 CurrentRoot);
