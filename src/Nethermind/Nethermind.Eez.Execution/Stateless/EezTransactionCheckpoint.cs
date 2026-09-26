// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// The state after a transaction prefix of a block, and the hash of the block that would end with that prefix.
/// </summary>
public readonly record struct EezTransactionCheckpoint(int TransactionIndex, Hash256 StateRoot, Hash256 BlockHash);
