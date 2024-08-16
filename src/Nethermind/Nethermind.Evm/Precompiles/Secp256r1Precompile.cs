// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nethermind.Evm.Precompiles;

public class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    static Secp256r1Precompile()
    {
        Environment.SetEnvironmentVariable("GODEBUG", "gctrace=1");
        Environment.SetEnvironmentVariable("GOGC", "1000");

        AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly())!.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

        var releaseSpec = new ReleaseSpec();
        var input = Convert.FromHexString(
            "4cee90eb86eaa050036147a12d49004b6b9c72bd725d39d4785011fe190f0b4da73bd4903f0ce3b639bbbf6e8e80d16931ff4bcf5993d58468e8fb19086e8cac36dbcd03009df8c59286b162af3bd7fcc0450c9aa81be5d10d312af6c66b1d604aebd3099c618202fcfe16ae7770b0c49ab5eadf74b754204a3bb6060e44eff37618b065f9832de4ca6ca971a7a1adc826d0f7c00181a5fb2ddf79ae00b4e10e"
        );

        var watch = Stopwatch.StartNew();
        Instance.Run(input, releaseSpec);
        Console.WriteLine($"[secp256r1] Warmed up in {watch.Elapsed}");
    }

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

    [DllImport("secp256r1", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    private static extern byte VerifyBytes(GoSlice hash);

    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        var count = ++Metrics.Secp256r1Precompile;
        var watch = Stopwatch.StartNew();

        if (count % 1000 == 0 || count <= 20)
            Console.WriteLine($"[{DateTime.UtcNow:T}] [secp256r1][{count:D5}] Starting");

        try
        {
            if (count <= 10)
            {
                if (inputData.Length != 160)
                    return (null, true);

                ReadOnlySpan<byte> bytes = inputData.Span;
                ReadOnlySpan<byte> hash = bytes[..32], sig = bytes[32..96];
                ReadOnlySpan<byte> x = bytes[96..128], y = bytes[128..160];

                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new() { X = x.ToArray(), Y = y.ToArray() }
                });

                return (ecdsa.VerifyHash(hash, sig) ? ValidResult : null, true);
            }
            else
            {
                using MemoryHandle pin = inputData.Pin();
                unsafe
                {
                    GoSlice slice = new((IntPtr) pin.Pointer, inputData.Length);
                    var res = VerifyBytes(slice);

                    TimeSpan elapsed = watch.Elapsed;
                    if (elapsed.TotalMicroseconds > 500)
                        Console.WriteLine($"[{DateTime.UtcNow:T}] [secp256r1][{count:D5}] Finished: {Convert.ToHexString(inputData.Span)} -> {res} in {elapsed.TotalMicroseconds}µs");

                    return (res != 0 ? ValidResult : null, true);
                }
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[{DateTime.UtcNow:T}] [secp256r1][{count:D5}] Failed: {exception.Message} in {watch.Elapsed}");
            throw;
        }
    }
}
