// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Buffers.Binary;

namespace Nethermind.Optimism;

public readonly struct L1TxGasInfo(
    UInt256? l1Fee,
    UInt256? l1GasPrice,
    UInt256? l1GasUsed,
    string? l1FeeScalar,
    UInt256? l1BaseFeeScalar = null,
    UInt256? l1BlobBaseFee = null,
    UInt256? l1BlobBaseFeeScalar = null,
    UInt32? operatorFeeScalar = null,
    UInt64? operatorFeeConstant = null)
{
    public UInt256? L1Fee { get; } = l1Fee;
    public UInt256? L1GasPrice { get; } = l1GasPrice;
    public UInt256? L1GasUsed { get; } = l1GasUsed;
    public string? L1FeeScalar { get; } = l1FeeScalar;

    public UInt256? L1BaseFeeScalar { get; } = l1BaseFeeScalar;
    public UInt256? L1BlobBaseFee { get; } = l1BlobBaseFee;
    public UInt256? L1BlobBaseFeeScalar { get; } = l1BlobBaseFeeScalar;

    public UInt32? OperatorFeeScalar { get; } = operatorFeeScalar;
    public UInt64? OperatorFeeConstant { get; } = operatorFeeConstant;
}

public sealed class L1BlockGasInfo
{
    private const int IsthmusDataLength = 176;
    private const int EcotoneDataLength = 164;

    private readonly UInt256? _l1GasPrice;
    private readonly UInt256? _l1BlobBaseFee;
    private readonly UInt256? _l1BaseFeeScalar;
    private readonly UInt256? _l1BlobBaseFeeScalar;
    private readonly UInt256 _l1BaseFee;
    private readonly UInt256 _overhead;
    private readonly UInt256 _feeScalar;
    private readonly string? _feeScalarDecimal;
    private readonly UInt32 _operatorFeeScalar;
    private readonly UInt64 _operatorFeeConstant;

    private readonly bool _isIsthmus;
    private readonly bool _isFjord;
    private readonly bool _isEcotone;
    private readonly bool _isPostRegolith;

    private static readonly byte[] BedrockL1AttributesSelector = [0x01, 0x5d, 0x8e, 0xb9];
    private readonly IOptimismSpecHelper _specHelper;

    public L1BlockGasInfo(Block block, IOptimismSpecHelper specHelper)
    {
        _specHelper = specHelper;

        if (block is not null && block.Transactions.Length > 0)
        {
            Transaction depositTx = block.Transactions[0];
            if (depositTx.Data is null || depositTx.Data.Value.Length < 4)
            {
                return;
            }

            Memory<byte> data = depositTx.Data.Value;

            _isIsthmus = _specHelper.IsIsthmus(block.Header);
            _isFjord = _specHelper.IsFjord(block.Header);
            _isPostRegolith = _specHelper.IsRegolith(block.Header);

            if (_isIsthmus)
            {
                if (data.Length < EcotoneDataLength)
                {
                    return;
                }

                ParsePostEcotoneBaseValues(data, out _l1GasPrice, out _l1BlobBaseFee, out _l1BaseFeeScalar, out _l1BlobBaseFeeScalar);

                if (data.Length != IsthmusDataLength)
                {
                    _operatorFeeScalar = 0;
                    _operatorFeeConstant = 0;
                    return;
                }

                // https://github.com/ethereum-optimism/specs/pull/382/files#diff-5ca81beda05e4bfca4ea5db10dcf59329ecc07861e3a710fd08359ebd2074379R27-R28
                _operatorFeeScalar = BinaryPrimitives.ReadUInt32BigEndian(data[EcotoneDataLength..168].Span);
                _operatorFeeConstant = BinaryPrimitives.ReadUInt64BigEndian(data[168..IsthmusDataLength].Span);
            }
            else if (_isFjord || (_isEcotone = (_specHelper.IsEcotone(block.Header) && !data[0..4].Span.SequenceEqual(BedrockL1AttributesSelector))))
            {
                if (data.Length != EcotoneDataLength)
                {
                    return;
                }

                ParsePostEcotoneBaseValues(data, out _l1GasPrice, out _l1BlobBaseFee, out _l1BaseFeeScalar, out _l1BlobBaseFeeScalar);
            }
            else
            {
                if (data.Length < 4 + 32 * 8)
                {
                    return;
                }

                _l1GasPrice = new(data[(4 + 32 * 2)..(4 + 32 * 3)].Span, true);
                _l1BaseFee = new(data[(4 + 32 * 2)..(4 + 32 * 3)].Span, true);
                _overhead = new(data[(4 + 32 * 6)..(4 + 32 * 7)].Span, true);
                _feeScalar = new UInt256(data[(4 + 32 * 7)..(4 + 32 * 8)].Span, true);
                _feeScalarDecimal = (((ulong)_feeScalar) / 1_000_000m).ToString();
            }
        }

        return;

        static void ParsePostEcotoneBaseValues(Memory<byte> data, out UInt256? l1GasPrice, out UInt256? l1BlobBaseFee,
            out UInt256? l1BaseFeeScalar, out UInt256? l1BlobBaseFeeScalar)
        {
            l1GasPrice = new(data[36..68].Span, true);
            l1BlobBaseFee = new(data[68..100].Span, true);
            l1BaseFeeScalar = new(data[4..8].Span, true);
            l1BlobBaseFeeScalar = new(data[8..12].Span, true);
        }
    }

    public L1TxGasInfo GetTxGasInfo(Transaction tx)
    {
        UInt256? l1Fee = null;
        UInt256? l1GasUsed = null;
        UInt32? operatorFeeScalar = null;
        UInt64? operatorFeeConstant = null;

        if (_l1GasPrice is not null)
        {
            if (_isIsthmus)
            {
                operatorFeeScalar = _operatorFeeScalar;
                operatorFeeConstant = _operatorFeeConstant;
            }
            if (_isFjord)
            {
                UInt256 fastLzSize = OptimismCostHelper.ComputeFlzCompressLen(tx);
                l1Fee = OptimismCostHelper.ComputeL1CostFjord(fastLzSize, _l1GasPrice.Value, _l1BlobBaseFee!.Value, _l1BaseFeeScalar!.Value, _l1BlobBaseFeeScalar!.Value, out UInt256 estimatedSize);
                l1GasUsed = OptimismCostHelper.ComputeGasUsedFjord(estimatedSize);
            }
            else if (_isEcotone)
            {
                l1GasUsed = OptimismCostHelper.ComputeDataGas(tx, _isPostRegolith);
                l1Fee = OptimismCostHelper.ComputeL1CostEcotone(l1GasUsed.Value, _l1GasPrice.Value, _l1BlobBaseFee!.Value, _l1BaseFeeScalar!.Value, _l1BlobBaseFeeScalar!.Value);
            }
            else
            {
                l1GasUsed = OptimismCostHelper.ComputeDataGas(tx, _isPostRegolith) + _overhead;
                l1Fee = OptimismCostHelper.ComputeL1CostPreEcotone(l1GasUsed.Value, _l1BaseFee, _feeScalar);
            }
        }

        return new L1TxGasInfo(l1Fee,
            _l1GasPrice,
            l1GasUsed,
            _feeScalarDecimal,
            _l1BaseFeeScalar,
            _l1BlobBaseFee,
            _l1BlobBaseFeeScalar,
            operatorFeeScalar,
            operatorFeeConstant);
    }
}
