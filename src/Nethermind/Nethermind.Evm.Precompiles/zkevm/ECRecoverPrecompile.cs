// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
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
        Span<byte> result = stackalloc byte[32]; // The first 12 bytes will be zero

        return EthereumEcdsa.RecoverAddressRaw(signature, recoveryId, message, result)
            ? result.ToArray() : Empty;
    }
}
