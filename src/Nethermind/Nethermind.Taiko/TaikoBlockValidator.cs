// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.TxPool;

namespace Nethermind.Taiko;

public class TaikoBlockValidator(
    ITxValidator txValidator,
    IHeaderValidator headerValidator,
    IUnclesValidator unclesValidator,
    ISpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    ILogManager logManager) : BlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager)
{
    private static readonly byte[] AnchorSelector = Keccak.Compute("anchor(bytes32,bytes32,uint64,uint32)").Bytes[0..4].ToArray();
    private static readonly byte[] AnchorV2Selector = Keccak.Compute("anchorV2(uint64,bytes32,uint32,(uint8,uint8,uint32,uint64,uint32))").Bytes[0..4].ToArray();
    private static readonly byte[] AnchorV3Selector = Keccak.Compute("anchorV3(uint64,bytes32,uint32,(uint8,uint8,uint32,uint64,uint32),bytes32[])").Bytes[0..4].ToArray();

    public static readonly Address GoldenTouchAccount = new("0x0000777735367b36bC9B61C50022d9D0700dB4Ec");

    private const long AnchorGasLimit = 250_000;
    private const long AnchorV3GasLimit = 1_000_000;

    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, out string? error)
    {
        // No blob transactions are expected, covered by ValidateTransactions also
        error = null;
        return true;
    }

    protected override bool ValidateTransactions(Block block, IReleaseSpec spec, out string? errorMessage)
    {
        if (block.IsGenesis)
        {
            errorMessage = null;
            return true;
        }

        if (block.TxRoot == Keccak.Zero)
        {
            if (block.Transactions.Length is 0)
            {
                errorMessage = "Missing required anchor transaction";
                return false;
            }

            if (!ValidateAnchorTransaction(block.Transactions[0], block, (ITaikoReleaseSpec)spec, out errorMessage))
                return false;
        }

        // TaikoPlugin initializes the TxValidator with a Always.Valid validator
        return base.ValidateTransactions(block, spec, out errorMessage);
    }

    private bool ValidateAnchorTransaction(Transaction tx, Block block, ITaikoReleaseSpec spec, out string? errorMessage)
    {
        if (tx.Type != TxType.EIP1559)
        {
            errorMessage = "Anchor transaction must be of type EIP-1559";
            return false;
        }

        if (tx.To != spec.FeeCollector)
        {
            errorMessage = "Anchor transaction must target Taiko L2 address";
            return false;
        }

        if (tx.Data.Length == 0
            || (!AnchorSelector.AsSpan().SequenceEqual(tx.Data.Span[0..4])
                && !AnchorV2Selector.AsSpan().SequenceEqual(tx.Data.Span[0..4])
                && !AnchorV3Selector.AsSpan().SequenceEqual(tx.Data.Span[0..4])))
        {
            errorMessage = "Anchor transaction must have valid selector";
            return false;
        }

        if (!tx.ValueRef.IsZero)
        {
            errorMessage = "Anchor transaction must have value of 0";
            return false;
        }

        if (tx.GasLimit != (spec.IsPacayaEnabled ? AnchorV3GasLimit : AnchorGasLimit))
        {
            errorMessage = "Anchor transaction must have correct gas limit";
            return false;
        }

        if (tx.MaxFeePerGas != block.BaseFeePerGas)
        {
            errorMessage = "Anchor transaction must have correct max fee per gas";
            return false;
        }

        tx.SenderAddress ??= ecdsa.RecoverAddress(tx);

        if (tx.SenderAddress is null)
        {
            errorMessage = "Anchor transaction sender address is not recoverable";
            return false;
        }

        if (!tx.SenderAddress!.Equals(GoldenTouchAccount))
        {
            errorMessage = "Anchor transaction must be sent by the golden touch account";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
