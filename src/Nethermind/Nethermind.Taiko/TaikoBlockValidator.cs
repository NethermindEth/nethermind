// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class TaikoBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    ILogManager logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    private static readonly byte[] AnchorSelector =
        KeccakHash.ComputeHash(Encoding.UTF8.GetBytes("anchor(bytes32,bytes32,uint64,uint32)"))[0..4].ToArray();

    private static readonly Address GoldenTouchAccount = new("0x0000777735367b36bC9B61C50022d9D0700dB4Ec");

    private readonly Address TaikoL2Address = new(specProvider.ChainId.ToString().TrimStart('0') + TaikoL2AddressSuffix.PadLeft(40, '0'));
    private const string TaikoL2AddressSuffix = "1001";

    private const long AnchorGasLimit = 250_000;

    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, out string? error)
    {
        // for some reason they don't validate these fields in taiko-geth
        error = null;
        return true;
    }

    protected override bool ValidateTransactions(Block block, IReleaseSpec spec, out string? errorMessage)
    {
        if (block.Transactions.Length == 0)
        {
            errorMessage = "Missing required Anchor Transaction.";
            return false;
        }

        if (!ValidateAnchorTransaction(block.Transactions[0], block, out errorMessage))
            return false;

        // TaikoPlugin initializes the TxValidator with a Always.Valid validator
        return base.ValidateTransactions(block, spec, out errorMessage);
    }

    private bool ValidateAnchorTransaction(Transaction tx, Block block, out string? errorMessage)
    {
        if (tx.Type != TxType.EIP1559)
        {
            errorMessage = "Anchor Transaction must be of type EIP1559.";
            return false;
        }

        if (tx.To is null || !tx.To.Equals(TaikoL2Address))
        {
            errorMessage = "Anchor Transaction must target taiko L2 address.";
            return false;
        }

        if (Bytes.AreEqual(tx.Data.AsArray().AsSpan(0..4), AnchorSelector))
        {
            errorMessage = "Anchor Transaction must have the correct selector.";
            return false;
        }

        if (tx.Value != 0)
        {
            errorMessage = "Anchor Transaction must have 0 value.";
            return false;
        }

        if (tx.GasLimit != AnchorGasLimit)
        {
            errorMessage = "Anchor Transaction must have the correct gas limit.";
            return false;
        }

        if (tx.MaxFeePerGas != block.BaseFeePerGas)
        {
            errorMessage = "Anchor Transaction must have the correct max fee per gas.";
            return false;
        }

        if (!tx.SenderAddress?.Equals(GoldenTouchAccount) ?? throw new InvalidOperationException("Sender address is null"))
        {
            errorMessage = "Anchor Transaction must be sent by the Golden Touch account.";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
