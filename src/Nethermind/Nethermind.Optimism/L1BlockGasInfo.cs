// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Linq;

namespace Nethermind.Optimism;

public readonly struct L1TxGasInfo(UInt256? l1Fee, UInt256? l1GasPrice, UInt256? l1GasUsed, string? l1FeeScalar)
{
    public UInt256? L1Fee { get; } = l1Fee;
    public UInt256? L1GasPrice { get; } = l1GasPrice;
    public UInt256? L1GasUsed { get; } = l1GasUsed;
    public string? L1FeeScalar { get; } = l1FeeScalar;
}

public readonly struct L1BlockGasInfo
{
    private readonly UInt256? _l1GasPrice;
    private readonly UInt256 _l1BlobBaseFee;
    private readonly UInt256 _l1BaseFeeScalar;
    private readonly UInt256 _l1BlobBaseFeeScalar;
    private readonly UInt256 _l1BaseFee;
    private readonly UInt256 _overhead;
    private readonly UInt256 _feeScalar;
    private readonly string? _feeScalarDecimal;
    private readonly bool _isFjord;
    private readonly bool _isEcotone;
    private readonly bool _isRegolith;

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

            _isFjord = _specHelper.IsFjord(block.Header);

            if (_isFjord || (_isEcotone = (_specHelper.IsEcotone(block.Header) && !data[0..4].Span.SequenceEqual(BedrockL1AttributesSelector))))
            {
                if (data.Length != 164)
                {
                    return;
                }

                _l1GasPrice = new(data[36..68].Span, true);
                _l1BlobBaseFee = new(data[68..100].Span, true);
                _l1BaseFeeScalar = new(data[4..8].Span, true);
                _l1BlobBaseFeeScalar = new(data[8..12].Span, true);
            }
            else
            {
                _isRegolith = true;
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
    }

    public readonly L1TxGasInfo GetTxGasInfo(Transaction tx)
    {
        UInt256? l1Fee = null;
        UInt256? l1GasUsed = null;

        if (_l1GasPrice is not null)
        {
            if (_isFjord)
            {
                UInt256 fastLzSize = OPL1CostHelper.ComputeFlzCompressLen(tx);
                l1Fee = OPL1CostHelper.ComputeL1CostFjord(fastLzSize, _l1GasPrice.Value, _l1BlobBaseFee, _l1BaseFeeScalar, _l1BlobBaseFeeScalar);
            }
            else if (_isEcotone)
            {
                l1GasUsed = OPL1CostHelper.ComputeDataGas(tx, _isRegolith);
                l1Fee = OPL1CostHelper.ComputeL1CostEcotone(l1GasUsed.Value, _l1GasPrice.Value, _l1BlobBaseFee, _l1BaseFeeScalar, _l1BlobBaseFeeScalar);
            }
            else
            {
                l1GasUsed = OPL1CostHelper.ComputeDataGas(tx, _isRegolith) + _overhead;
                l1Fee = OPL1CostHelper.ComputeL1CostPreEcotone(l1GasUsed.Value, _l1BaseFee, _feeScalar);
            }
        }

        return new L1TxGasInfo(l1Fee, _l1GasPrice, l1GasUsed, _feeScalarDecimal);
    }
}
