// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.History;

public interface IHistoryPruner
{
    public long? CutoffBlockNumber { get; }
    public BlockHeader? OldestBlockHeader { get; }

    event EventHandler<OnNewOldestBlockArgs> NewOldestBlock;
}
