// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// <c>keccak256(abi.encode(isStatic, sourceAddress, uint64 sourceRollupId, targetAddress, uint64 targetRollupId,
/// uint256 value, uint64 callGas, bytes data))</c>, the identity of a cross-chain call.
/// </summary>
public static class CrossChainCallHash
{
    private const int HeadWords = 8;
    private const int StackLimit = 512;

    /// <param name="callGas">Zero, except for a call leaving an L2 whose manager folds the observed gas.</param>
    public static ValueHash256 Compute(bool isStatic, Address sourceAddress, ulong sourceRollupId, Address targetAddress,
        ulong targetRollupId, in UInt256 value, ulong callGas, ReadOnlySpan<byte> data)
    {
        int length = (HeadWords + 1) * AbiWord.Size + AbiWord.PaddedLength(data.Length);
        byte[]? rented = length > StackLimit ? ArrayPool<byte>.Shared.Rent(length) : null;
        try
        {
            Span<byte> buffer = rented is null ? stackalloc byte[StackLimit] : rented;
            buffer = buffer[..length];
            AbiWord.Write(buffer[..32], isStatic);
            AbiWord.Write(buffer[32..64], sourceAddress);
            AbiWord.Write(buffer[64..96], sourceRollupId);
            AbiWord.Write(buffer[96..128], targetAddress);
            AbiWord.Write(buffer[128..160], targetRollupId);
            AbiWord.Write(buffer[160..192], value);
            AbiWord.Write(buffer[192..224], callGas);
            AbiWord.Write(buffer[224..256], HeadWords * AbiWord.Size);
            AbiWord.Write(buffer[256..288], (ulong)data.Length);
            data.CopyTo(buffer[288..]);
            buffer[(288 + data.Length)..].Clear();
            return ValueKeccak.Compute(buffer);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
