// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.Test.Helpers;

public static class TransactionBuilderXdcExtensions
{
    // function sign(uint256 _blockNumber, bytes32 _blockHash)
    // selector = 0xe341eaa4
    private static ReadOnlySpan<byte> SignSelector => new byte[] { 0xE3, 0x41, 0xEA, 0xA4 };

    /// <summary>Sets 'To' to the XDC block-signer contract from the spec.</summary>
    public static TransactionBuilder<Transaction> ToBlockSignerContract(
        this TransactionBuilder<Transaction> b, IXdcReleaseSpec spec)
        => b.To(spec.BlockSignerContract);

    /// <summary>
    /// Appends ABI-encoded calldata for sign(uint256 _blockNumber, bytes32 _blockHash).
    /// Calldata = 4-byte selector + 32-byte big-endian uint + 32-byte bytes32 (68 bytes total).
    /// </summary>
    public static TransactionBuilder<Transaction> WithXdcSigningData(
        this TransactionBuilder<Transaction> b, long blockNumber, Hash256 blockHash)
        => b.WithData(CreateSigningCalldata(blockNumber, blockHash));

    private static byte[] CreateSigningCalldata(long blockNumber, Hash256 blockHash)
    {
        Span<byte> data = stackalloc byte[68]; // 4 + 32 + 32

        // 0..3: selector
        SignSelector.CopyTo(data);

        // 4..35: uint256 blockNumber (big-endian, right-aligned in 32 bytes)
        var be = BitConverter.GetBytes((ulong)blockNumber);
        if (BitConverter.IsLittleEndian) Array.Reverse(be);
        // last 8 bytes of that 32 are the ulong
        for (int i = 0; i < 8; i++) data[4 + 24 + i] = be[i];

        // 36..67: bytes32 blockHash
        blockHash.Bytes.CopyTo(data.Slice(36, 32));

        return data.ToArray();
    }
}
