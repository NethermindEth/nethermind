// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Stateless;

public sealed record EezStatelessBlockResult(
    Block Block,
    Hash256 PreStateRoot,
    TxReceipt[] Receipts,
    EezTransactionCheckpoint[] Checkpoints)
{
    public Hash256 Hash => Block.Hash!;

    public Hash256 PostStateRoot => Block.Header.StateRoot!;
}
