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
    private readonly ILogger _logger = logManager.GetClassLogger<TaikoBlockValidator>();
    private static readonly byte[] AnchorSelector = Keccak.Compute("anchor(bytes32,bytes32,uint64,uint32)").Bytes[..4].ToArray();
    private static readonly byte[] AnchorV2Selector = Keccak.Compute("anchorV2(uint64,bytes32,uint32,(uint8,uint8,uint32,uint64,uint32))").Bytes[..4].ToArray();
    private static readonly byte[] AnchorV3Selector = Keccak.Compute("anchorV3(uint64,bytes32,uint32,(uint8,uint8,uint32,uint64,uint32),bytes32[])").Bytes[..4].ToArray();
    public static readonly byte[] AnchorV4Selector = Keccak.Compute("anchorV4((uint48,bytes32,bytes32))").Bytes[..4].ToArray();
    public static readonly byte[] AnchorV4WithSignalSlotsSelector = Keccak.Compute("anchorV4WithSignalSlots((uint48,bytes32,bytes32),bytes32[])").Bytes[..4].ToArray();


    public static readonly Address GoldenTouchAccount = new("0x0000777735367b36bC9B61C50022d9D0700dB4Ec");

    private const ulong AnchorGasLimit = 250_000UL;
    private const ulong AnchorV3V4GasLimit = 1_000_000UL;

    // Blob transactions are explicitly rejected in ValidateTransactions below; no extra EIP-4844 field
    // validation is needed on Taiko L2.
    protected override bool ValidateEip4844Fields(Block block, IReleaseSpec spec, ref string? error) => true;

    protected override bool ValidateTransactions(Block block, IReleaseSpec spec, ref string? errorMessage)
    {
        if (block.IsGenesis)
        {
            return true;
        }

        if (block.Transactions.Length is not 0 && !ValidateAnchorTransaction(block.Transactions[0], block, (ITaikoReleaseSpec)spec, out errorMessage))
        {
            return false;
        }

        // Blob transactions are never valid on Taiko L2. The base TxValidator is initialised
        // with an Always.Valid validator (see TaikoPlugin), so we must enforce this explicitly.
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.Type == TxType.Blob)
            {
                errorMessage = "Blob transactions are not supported on Taiko L2";
                return false;
            }
        }

        // TaikoPlugin initializes the TxValidator with an Always.Valid validator
        return base.ValidateTransactions(block, spec, ref errorMessage);
    }

    private bool ValidateAnchorTransaction(Transaction tx, Block block, ITaikoReleaseSpec spec, out string? errorMessage)
    {
        if (_logger.IsDebug)
        {
            _logger.Debug($"ValidateAnchorTransaction: Type={tx.Type}, To={tx.To}, DataLength={tx.Data.Length}, " +
                $"Data[..min(8,len)]=0x{Convert.ToHexString(tx.Data.Span[..Math.Min(8, tx.Data.Length)])}, " +
                $"GasLimit={tx.GasLimit}, MaxFeePerGas={tx.MaxFeePerGas}, BaseFee={block.BaseFeePerGas}, " +
                $"TaikoL2Address={spec.TaikoL2Address}");
        }

        if (tx.Type != TxType.EIP1559)
        {
            errorMessage = "Anchor transaction must be of type EIP-1559";
            return false;
        }

        if (tx.To != spec.TaikoL2Address)
        {
            errorMessage = "Anchor transaction must target Taiko L2 address";
            return false;
        }

        if (tx.Data.Length < 4 || !IsValidAnchorSelector(tx.Data.Span[..4], spec))
        {
            errorMessage = "Anchor transaction must have valid selector for the current fork";
            return false;
        }

        if (tx.ValueRef != 0)
        {
            errorMessage = "Anchor transaction must have value of 0";
            return false;
        }

        if (tx.GasLimit != (spec.IsPacayaEnabled || spec.IsShastaEnabled ? AnchorV3V4GasLimit : AnchorGasLimit))
        {
            errorMessage = "Anchor transaction must have correct gas limit";
            return false;
        }

        if (tx.MaxFeePerGas != block.BaseFeePerGas)
        {
            errorMessage = "Anchor transaction must have correct max fee per gas";
            return false;
        }

        // We don't set the tx.SenderAddress here, as it will stop the rest of the transactions in the block
        // from getting their sender address recovered
        Address? senderAddress = tx.SenderAddress ?? ecdsa.RecoverAddress(tx);

        if (senderAddress is null)
        {
            errorMessage = "Anchor transaction sender address is not recoverable";
            return false;
        }

        if (!senderAddress.Equals(GoldenTouchAccount))
        {
            errorMessage = "Anchor transaction must be sent by the golden touch account";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private bool IsValidAnchorSelector(ReadOnlySpan<byte> selector, ITaikoReleaseSpec spec)
    {
        if (_logger.IsDebug)
        {
            _logger.Debug($"Anchor selector: 0x{Convert.ToHexString(selector)}, " +
                $"IsUnzenEnabled={spec.IsUnzenEnabled}, IsShastaEnabled={spec.IsShastaEnabled}, " +
                $"IsPacayaEnabled={spec.IsPacayaEnabled}, IsOntakeEnabled={spec.IsOntakeEnabled}. " +
                $"Expected: {(spec.IsShastaEnabled ? $"V4=0x{Convert.ToHexString(AnchorV4Selector)} or V4WithSignalSlots=0x{Convert.ToHexString(AnchorV4WithSignalSlotsSelector)}" : spec.IsPacayaEnabled ? $"V3=0x{Convert.ToHexString(AnchorV3Selector)}" : $"V1=0x{Convert.ToHexString(AnchorSelector)} or V2=0x{Convert.ToHexString(AnchorV2Selector)}")}");
        }

        if (spec.IsShastaEnabled)
            return AnchorV4Selector.AsSpan().SequenceEqual(selector)
                || AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(selector);

        if (spec.IsPacayaEnabled)
            return AnchorV3Selector.AsSpan().SequenceEqual(selector);

        return AnchorSelector.AsSpan().SequenceEqual(selector)
            || AnchorV2Selector.AsSpan().SequenceEqual(selector);
    }
}
