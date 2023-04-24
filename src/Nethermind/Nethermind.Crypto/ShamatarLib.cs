// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Reflection;

namespace Nethermind.Crypto.Bls;

public static class ShamatarLib
{
    private static readonly int _loaded;

    static ShamatarLib()
    {
        if (Interlocked.CompareExchange(ref _loaded, 1, 0) == 0)
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), LoadLibrary);
    }

    [DllImport("shamatar")]
    private static extern unsafe uint eip196_perform_operation(
        byte operation,
        byte* input,
        int inputLength,
        byte* output,
        ref int outputLength,
        byte* error,
        ref int errorLength);

    [DllImport("shamatar")]
    private static extern unsafe uint eip2537_perform_operation(
        byte operation,
        byte* input,
        int inputLength,
        byte* output,
        ref int outputLength,
        byte* error,
        ref int errorLength);

    private static unsafe bool Bn256Op(byte operation, ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outputLength = output.Length;
        int errorLength = 256;
        uint externalCallResult;

        Span<byte> error = stackalloc byte[errorLength];
        fixed (byte* inputPtr = &MemoryMarshal.GetReference(input))
        fixed (byte* outputPtr = &MemoryMarshal.GetReference(output))
        fixed (byte* errorPtr = &MemoryMarshal.GetReference(error))
        {
            externalCallResult = eip196_perform_operation(
                operation, inputPtr, input.Length, outputPtr, ref outputLength, errorPtr, ref errorLength);
        }

        return externalCallResult == 0;
    }

    private static unsafe bool BlsOp(byte operation, ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outputLength = output.Length;
        int errorLength = 256;
        uint externalCallResult;

        Span<byte> error = stackalloc byte[errorLength];
        fixed (byte* inputPtr = &MemoryMarshal.GetReference(input))
        fixed (byte* outputPtr = &MemoryMarshal.GetReference(output))
        fixed (byte* errorPtr = &MemoryMarshal.GetReference(error))
        {
            externalCallResult = eip2537_perform_operation(
                operation, inputPtr, input.Length, outputPtr, ref outputLength, errorPtr, ref errorLength);
        }

        return externalCallResult == 0;
    }

    public static bool Bn256Add(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return Bn256Op(1, input, output);
    }

    public static bool Bn256Mul(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return Bn256Op(2, input, output);
    }

    public static bool Bn256Pairing(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return Bn256Op(3, input, output);
    }

    public static bool BlsG1Add(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(1, input, output);
    }

    public static bool BlsG1Mul(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(2, input, output);
    }

    public static bool BlsG1MultiExp(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(3, input, output);
    }

    public static bool BlsG2Add(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(4, input, output);
    }

    public static bool BlsG2Mul(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(5, input, output);
    }

    public static bool BlsG2MultiExp(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(6, input, output);
    }

    public static bool BlsPairing(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(7, input, output);
    }

    public static bool BlsMapToG1(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(8, input, output);
    }

    public static bool BlsMapToG2(ReadOnlySpan<byte> input, Span<byte> output)
    {
        return BlsOp(9, input, output);
    }

    private static nint LoadLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string platform;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            libraryName = $"lib{libraryName}.so";
            platform = "linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            libraryName = $"{libraryName}.dll";
            platform = "win";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            libraryName = $"lib{libraryName}.dylib";
            platform = "osx";
        }
        else
            throw new PlatformNotSupportedException();

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        return NativeLibrary.Load(
            Path.Combine("runtimes", $"{platform}-{arch}", "native", libraryName), assembly, searchPath);
    }
}
