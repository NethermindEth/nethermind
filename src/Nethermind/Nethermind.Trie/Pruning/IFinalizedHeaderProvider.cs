// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Trie.Pruning;

public interface IFinalizedHeaderProvider
{
    // Note: Finalized header must never be null. It could be very old, or genesis, but it can never be null.
    BlockHeader FinalizedHeader { get; }
}
