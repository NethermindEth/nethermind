// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class TransferLog
{
    // keccak256('Transfer(address,address,uint256)')
    public static readonly Hash256 TransferSignature = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
    // keccak256('Burn(address,uint256)')
    public static readonly Hash256 BurnSignature = new("0xcc16f5dbb4873280815c1ee09dbd06736cffcc184412cf7a71a0fdb75d397ca5");
    public static readonly Address Sender = Address.SystemUser;
    public static readonly Address Erc20Sender = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

    public static LogEntry CreateTransfer(Address from, Address to, in UInt256 amount) =>
        CreateTransferInternal(Sender, from, to, amount);

    public static LogEntry CreateBurn(Address account, in UInt256 amount) =>
        new(Sender, amount.ToBigEndian(), [BurnSignature, account.ToHash().ToHash256()]);

    public static LogEntry CreateSimulateTransfer(Address from, Address to, in UInt256 amount) =>
        CreateTransferInternal(Erc20Sender, from, to, amount);

    private static LogEntry CreateTransferInternal(Address sender, Address from, Address to, in UInt256 amount) =>
        new(sender, amount.ToBigEndian(), [TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]);
}
