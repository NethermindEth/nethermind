// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

public partial class EcRecoverPrecompile
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Result<byte[]> Recover(
        ReadOnlySpan<byte> signature,
        byte recoveryId,
        ReadOnlySpan<byte> message)
    {
        Span<byte> result = stackalloc byte[32];

        return EthereumEcdsa.RecoverAddressRaw(signature, recoveryId, message, result)
            ? result.ToArray() : Empty;
    }
}
