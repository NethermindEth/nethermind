// Copyright 2022 Demerzel Solutions Limited
// Licensed under Apache-2.0.For full terms, see LICENSE in the project root.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Nethermind.Verkle;

public static partial class RustVerkleLib
{
    private const string LibraryName = "c_verkle";
    private static readonly int _initialized;
    private static string? _libraryFallbackPath;

    static RustVerkleLib()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(RustVerkleLib).Assembly, ResolveNativeLibrary);
    }

    [LibraryImport("c_verkle", EntryPoint = "context_new")]
    public static partial IntPtr VerkleContextNew();

    [LibraryImport("c_verkle", EntryPoint = "context_free")]
    public static partial void VerkleContextFree(IntPtr ct);

    [LibraryImport("c_verkle", EntryPoint = "pedersen_hash")]
    public static partial void PedersenHash(IntPtr ct, byte[] address, byte[] treeIndexLe, byte[] outHash);

    [LibraryImport("c_verkle", EntryPoint = "pedersen_hash_flat")]
    public static unsafe partial void PedersenHashFlat(IntPtr ct, byte* addAndTreeIndexLe, byte[] outHash);

    [LibraryImport("c_verkle", EntryPoint = "multi_scalar_mul")]
    public static partial void MultiScalarMul(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    /// Receives a tuple (C_i, f_i(X), z_i, y_i)
    /// Where C_i is a commitment to f_i(X) serialized as 32 bytes
    /// f_i(X) is the polynomial serialized as 8192 bytes since we have 256 Fr elements each serialized as 32 bytes
    /// z_i is index of the point in the polynomial: 1 byte (number from 1 to 256)
    /// y_i is the evaluation of the polynomial at z_i i.e value we are opening: 32 bytes
    /// Returns a proof serialized as bytes
    ///
    /// This function assumes that the domain is always 256 values and commitment is 32bytes.
    [LibraryImport("c_verkle", EntryPoint = "create_proof")]
    public static partial void Prove(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    /// Receives a proof and a tuple (C_i, z_i, y_i)
    /// Where C_i is a commitment to f_i(X) serialized as 64 bytes (uncompressed commitment)
    /// z_i is index of the point in the polynomial: 1 byte (number from 1 to 256)
    /// y_i is the evaluation of the polynomial at z_i i.e value we are opening: 32 bytes or Fr (scalar field element)
    /// Returns true of false.
    /// Proof is verified or not.
    [LibraryImport("c_verkle", EntryPoint = "verify_proof")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool Verify(IntPtr ct, byte[] input, UIntPtr length);

    [LibraryImport("c_verkle", EntryPoint = "create_proof_uncompressed")]
    public static partial void ProveUncompressed(IntPtr ct, byte[] input, UIntPtr length, byte[] outHash);

    [LibraryImport("c_verkle", EntryPoint = "verify_proof_uncompressed")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool VerifyUncompressed(IntPtr ct, byte[] input, UIntPtr length);

    [LibraryImport("c_verkle", EntryPoint = "get_leaf_delta_both_value")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool GetLeafDeltaBothValue(IntPtr ct, byte subIndex, byte[] oldValue, byte[] newValue,
        byte[] output);

    [LibraryImport("c_verkle", EntryPoint = "get_leaf_delta_new_value")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool GetLeafDeltaNewValue(IntPtr ct, byte subIndex, byte[] newValue, byte[] output);

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_libraryFallbackPath is null)
        {
            string platform;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                libraryName = $"lib{libraryName}.so";
                platform = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                libraryName = $"lib{libraryName}.dylib";
                platform = "osx";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                libraryName = $"{libraryName}.dll";
                platform = "win";
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            _libraryFallbackPath = Path.Combine("runtimes", $"{platform}-{arch}", "native", libraryName);
        }

        NativeLibrary.TryLoad(_libraryFallbackPath, assembly, searchPath, out var handle);
        return handle;
    }
}
