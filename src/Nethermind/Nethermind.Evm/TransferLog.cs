// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class TransferLog
{
    // keccak256('Transfer(address,address,uint256)')
    private static readonly Hash256 TransferSignature = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
    private static readonly Address Erc20Sender = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

    public static LogEntry CreateTransfer(Address from, Address to, in UInt256 amount) =>
        new(Erc20Sender, amount.ToBigEndian(), [TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]);
}
