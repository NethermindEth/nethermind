// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Result<byte[]> Recover(
        ReadOnlySpan<byte> signature,
        byte recoveryId,
        ReadOnlySpan<byte> message)
    {
        Span<byte> publicKey = stackalloc byte[65];

        if (!EthereumEcdsa.RecoverAddressRaw(signature, recoveryId, message, publicKey))
            return Empty;

        byte[] result = new byte[32];
        ref byte refResult = ref MemoryMarshal.GetArrayDataReference(result);

        KeccakCache.ComputeTo(publicKey.Slice(1, 64), out Unsafe.As<byte, ValueHash256>(ref refResult));

        // Clear first 12 bytes, as address is last 20 bytes of the hash
        Unsafe.InitBlockUnaligned(ref refResult, 0, 12);
        return result;
    }
}
