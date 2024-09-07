// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Nethermind.Verkle;

public static class RustVerkleLib
{
    private const string LibraryName = "c_verkle";
    private static string? _libraryFallbackPath;
    static RustVerkleLib()
    {
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
    }

    [DllImport("c_verkle", EntryPoint = "context_new", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr VerkleContextNew();

    [DllImport("c_verkle", EntryPoint = "context_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VerkleContextFree(IntPtr ct);

    [DllImport("c_verkle", EntryPoint = "pedersen_hash", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VerklePedersenhash(IntPtr ct, byte[] address, byte[] treeIndexLe, byte[] outHash);

    [DllImport("c_verkle", EntryPoint = "multi_scalar_mul", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VerkleMSM(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    /// Receives a tuple (C_i, f_i(X), z_i, y_i)
    /// Where C_i is a commitment to f_i(X) serialized as 32 bytes
    /// f_i(X) is the polynomial serialized as 8192 bytes since we have 256 Fr elements each serialized as 32 bytes
    /// z_i is index of the point in the polynomial: 1 byte (number from 1 to 256)
    /// y_i is the evaluation of the polynomial at z_i i.e value we are opening: 32 bytes
    /// Returns a proof serialized as bytes
    ///
    /// This function assumes that the domain is always 256 values and commitment is 32bytes.
    [DllImport("c_verkle", EntryPoint = "create_proof", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VerkleProve(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    /// Receives a proof and a tuple (C_i, z_i, y_i)
    /// Where C_i is a commitment to f_i(X) serialized as 64 bytes (uncompressed commitment)
    /// z_i is index of the point in the polynomial: 1 byte (number from 1 to 256)
    /// y_i is the evaluation of the polynomial at z_i i.e value we are opening: 32 bytes or Fr (scalar field element)
    /// Returns true of false.
    /// Proof is verified or not.
    [DllImport("c_verkle", EntryPoint = "verify_proof", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VerkleVerify(IntPtr ct, byte[] input, UIntPtr length);

    [DllImport("c_verkle", EntryPoint = "create_proof_uncompressed", CallingConvention = CallingConvention.Cdecl)]
    public static extern void VerkleProveUncompressed(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    [DllImport("c_verkle", EntryPoint = "verify_proof_uncompressed", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VerkleVerifyUncompressed(IntPtr ct, byte[] input, UIntPtr length);

    private static nint OnResolvingUnmanagedDll(Assembly context, string name)
    {
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

            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            _libraryFallbackPath = Path.Combine("runtimes", $"{platform}-{arch}", "native", name);
        }

        return NativeLibrary.Load(_libraryFallbackPath, context, default);
    }
}
