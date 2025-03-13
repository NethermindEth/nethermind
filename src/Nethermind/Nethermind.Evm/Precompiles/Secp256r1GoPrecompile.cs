// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public partial class Secp256r1GoPrecompile : IPrecompile<Secp256r1GoPrecompile>
{
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1GoPrecompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    [LibraryImport("Binaries/go/secp256r1", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial byte VerifyBytes(byte* data, int length);

    [LibraryImport("Binaries/go/secp256r1", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial void ForceGC();

    [LibraryImport("Binaries/go/secp256r1", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe partial void ReportGC();

    public unsafe (byte[], bool) Run(ReadOnlyMemory<byte> input, IReleaseSpec releaseSpec)
    {
        Console.WriteLine($"Secp256r1GoPrecompile: {Convert.ToHexString(input.Span)} ...");

        bool isValid;
        fixed (byte* ptr = input.Span)
        {
            isValid = VerifyBytes(ptr, input.Length) != 0;
            Console.WriteLine($"Secp256r1GoPrecompile: {Convert.ToHexString(input.Span)} -> {isValid}");
        }

        Metrics.Secp256r1Precompile++;

        Console.WriteLine("Secp256r1GoPrecompile: Forcing Go GC");
        ForceGC();
        Console.WriteLine("Secp256r1GoPrecompile: Forced Go GC");

        return (isValid ? ValidResult : null, true);
    }
}
