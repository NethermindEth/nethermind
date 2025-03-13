// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

// https://github.com/oreparaz/p256
public partial class Secp256r1P256Precompile : IPrecompile<Secp256r1P256Precompile>
{
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1P256Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [LibraryImport("Binaries/p256/p256", SetLastError = true, EntryPoint = "p256_verify_prehash")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int VerifyBytes(byte* hash, byte* sig, byte* pk);

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> input, IReleaseSpec releaseSpec)
    {
        bool isValid;

        var publicKey = new byte[65];
        publicKey[0] = 4;
        input.Span[96..].CopyTo(publicKey.AsSpan(1..));

        fixed (byte* ptr = input.Span)
        fixed (byte* pk = publicKey)
        {
            var res = VerifyBytes(ptr, ptr + 32, pk);
            isValid = res == 1;
        }

        Metrics.Secp256r1Precompile++;

        return (isValid ? ValidResult : null, true);
    }
}
