// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Optimism;

public class L1GasInfo
{
    public UInt256? L1BaseFeeScalar;
    public UInt256? L1BlobBaseFee;
    public UInt256? L1BlobBaseFeeScalar;
    public UInt256? L1Fee;
    public UInt256? L1GasPrice;
    public UInt256? L1GasUsed;
}
