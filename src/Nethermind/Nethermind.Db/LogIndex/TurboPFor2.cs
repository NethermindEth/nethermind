// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Loader;

namespace Nethermind.Db.LogIndex;

public static partial class TurboPFor2
{
    private const string LibraryName = @"C:\Users\alexb\.nuget\packages\nethermind.turbopforbindings\1.0.0-preview.2\runtimes\win-x64\native\ic.dll";
    private static string? _libraryFallbackPath;

    /// <summary>
    /// If <c>false</c>, methods using 256 blocks will throw an exception.
    /// </summary>
    public static bool Supports256Blocks => Avx2.IsSupported;

    static TurboPFor2() => AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

    [LibraryImport(LibraryName)]
    public static partial nuint p4nd1enc64(ReadOnlySpan<long> @in, nuint n, Span<byte> @out);

    [LibraryImport(LibraryName)]
    public static partial nuint p4nd1dec64(ReadOnlySpan<byte> @in, nuint n, Span<long> @out);

    private static IntPtr OnResolvingUnmanagedDll(Assembly context, string name)
    {
        if (!LibraryName.Equals(name, StringComparison.Ordinal))
            return nint.Zero;

        if (_libraryFallbackPath is null)
        {
            string platform;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                name = $"lib{name}.so";
                platform = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                name = $"lib{name}.dylib";
                platform = "osx";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                name = $"{name}.dll";
                platform = "win";
            }
            else
                throw new PlatformNotSupportedException();

            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            _libraryFallbackPath = Path.Combine("runtimes", $"{platform}-{arch}", "native", name);
        }

        return NativeLibrary.Load(_libraryFallbackPath, context, default);
    }
}
