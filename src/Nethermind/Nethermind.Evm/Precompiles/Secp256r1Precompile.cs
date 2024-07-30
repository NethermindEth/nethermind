// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    static Secp256r1Precompile() => AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

    private static IntPtr OnResolvingUnmanagedDll(Assembly context, string name)
    {
        if (name != "secp256r1")
            return IntPtr.Zero;

        string platform, extension;
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            extension = "so";
            platform = "linux";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            extension = "dylib";
            platform = "osx";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            extension = "dll";
            platform = "win";
        }
        else
        {
            throw new PlatformNotSupportedException();
        }

        name = $"{name}.{platform}-{arch}.{extension}";
        return NativeLibrary.Load(name, context, default);
    }

    private struct GoSlice(IntPtr data, long len)
    {
        public IntPtr Data = data;
        public long Len = len, Cap = len;
    }

    [DllImport("secp256r1", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte VerifyBytes(GoSlice hash);

    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        ReadOnlySpan<byte> input = inputData.Span;

        GoSlice slice;
        unsafe
        {
            fixed (byte* p = input)
            {
                var ptr = (IntPtr) p;
                slice = new(ptr, input.Length);
            }
        }

        return (VerifyBytes(slice) != 0 ? ValidResult : null, true);
    }
}
