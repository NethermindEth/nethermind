// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Specs.ChainSpecStyle;

public class GenesisBlock : Block
{
    public GenesisBlock(BlockHeader blockHeader, BlockBody body)
        : base(blockHeader, body)
    {
    }

    public GenesisBlock(
        BlockHeader blockHeader,
        IEnumerable<Transaction> transactions,
        IEnumerable<BlockHeader> uncles,
        IEnumerable<Withdrawal> withdrawals = null) : base(blockHeader, transactions, uncles, withdrawals){ }

    public GenesisBlock(BlockHeader blockHeader)
        : base(blockHeader)
    {
    }

    public Address ConstructorSender { get; set; }
}
