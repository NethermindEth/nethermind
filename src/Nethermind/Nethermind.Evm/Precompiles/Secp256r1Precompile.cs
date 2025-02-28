// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    private struct GoSlice(nint data, long len)
    {
        public readonly nint Data = data;
        public readonly long Len = len, Cap = len;
    }

    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [LibraryImport("Binaries/secp256r1", SetLastError = true)]
    private static partial byte VerifyBytes(GoSlice hash);

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> input, IReleaseSpec releaseSpec)
    {
        using var pin = input.Pin();
        GoSlice slice = new((nint)pin.Pointer, input.Length);
        var isValid = VerifyBytes(slice) != 0;

        Metrics.Secp256r1Precompile++;

        return (isValid ? ValidResult : null, true);
    }
}
