// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public interface IFinalizedHeaderProvider
{
    long FinalizedBlockNumber { get; }
    Hash256? GetFinalizedStateRootAt(long blockNumber);
}
