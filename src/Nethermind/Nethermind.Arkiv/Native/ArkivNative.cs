// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Nethermind.Arkiv.Native;

/// <summary>What a call into the native library came to; mirrors <c>arkiv_ffi::Status</c>.</summary>
public enum ArkivStatus
{
    Ok = 0,
    StateRootMismatch = 1,
    InvalidBlock = 2,
    NotOnHead = 3,
    BadInput = 4,
    Internal = 5,
}

/// <summary>What <see cref="ArkivNative.ExecuteBlock"/> does with a block that executes to its header's root; mirrors <c>arkiv_ffi::Apply</c>.</summary>
public enum ArkivApply
{
    DryRun = 0,
    Commit = 1,
    /// <summary>Sealed but not committed, until <see cref="ArkivNative.Adopt"/>: a sibling executes over the same head meanwhile.</summary>
    Hold = 2,
}

/// <summary>A native call that did not succeed, with the native side's message.</summary>
public sealed class ArkivNativeException(ArkivStatus status, string message) : Exception($"{status}: {message}")
{
    public ArkivStatus Status { get; } = status;
    public string NativeMessage { get; } = message;
}

/// <summary>
/// The C ABI of <c>libarkiv_ffi</c> (<c>native/arkiv-ffi</c>): Arkiv's own executor and GolemDB store. Every buffer the
/// library returns is copied out and freed here, so nothing native outlives a call.
/// </summary>
internal static unsafe partial class ArkivNative
{
    private const string Library = "arkiv_ffi";
    private static string? _libraryPath;
    private static int _resolverSet;

    /// <summary>Points the loader at <paramref name="path"/>, or at the library beside the plugin when null.</summary>
    /// <remarks>The library loads once per process, so only the first path counts.</remarks>
    public static void Configure(string? path)
    {
        _libraryPath ??= path ?? Path.Combine(Path.GetDirectoryName(typeof(ArkivNative).Assembly.Location)!, "libarkiv_ffi.so");
        if (Interlocked.Exchange(ref _resolverSet, 1) == 0) NativeLibrary.SetDllImportResolver(typeof(ArkivNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && _libraryPath is not null && File.Exists(_libraryPath)
            ? NativeLibrary.Load(_libraryPath)
            : IntPtr.Zero;

    [LibraryImport(Library)]
    private static partial int arkiv_open(byte* genesis, nuint genesisLen, byte* dir, nuint dirLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    internal static partial void arkiv_close(ulong handle);

    [LibraryImport(Library)]
    private static partial void arkiv_free(byte* ptr, nuint len);

    [LibraryImport(Library)]
    private static partial int arkiv_head(ulong handle, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_execute_block(ulong handle, byte* block, nuint blockLen, byte* senders, nuint sendersLen, int apply, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_adopt(ulong handle, byte* hash, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_account(ulong handle, byte* address, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_call(ulong handle, byte* request, nuint requestLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_rpc(ulong handle, byte* method, nuint methodLen, byte* parameters, nuint parametersLen, ulong headNumber, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_purge_keys(ulong handle, ulong block, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_begin(ulong handle, byte* header, nuint headerLen, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_apply(ulong session, byte* tx, nuint txLen, byte* sender, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    private static partial int arkiv_build_finish(ulong session, out byte* outPtr, out nuint outLen);

    [LibraryImport(Library)]
    internal static partial void arkiv_build_abort(ulong session);

    [LibraryImport(Library)]
    private static partial int arkiv_store_accounts(byte* dir, nuint dirLen, out byte* outPtr, out nuint outLen);

    /// <summary>Copies the returned buffer out, frees it, and throws for any status but <see cref="ArkivStatus.Ok"/>.</summary>
    private static byte[] Take(int status, byte* ptr, nuint len)
    {
        byte[] bytes = len == 0 ? [] : new ReadOnlySpan<byte>(ptr, checked((int)len)).ToArray();
        arkiv_free(ptr, len);
        return status == (int)ArkivStatus.Ok ? bytes : throw new ArkivNativeException((ArkivStatus)status, Encoding.UTF8.GetString(bytes));
    }

    public static byte[] Open(string genesisJson, string? directory)
    {
        byte[] genesis = Encoding.UTF8.GetBytes(genesisJson);
        byte[] dir = Encoding.UTF8.GetBytes(directory ?? string.Empty);
        fixed (byte* g = genesis)
        fixed (byte* d = dir)
        {
            int status = arkiv_open(g, (nuint)genesis.Length, d, (nuint)dir.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    public static byte[] Head(ulong handle)
    {
        int status = arkiv_head(handle, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }

    public static byte[] ExecuteBlock(ulong handle, ReadOnlySpan<byte> blockRlp, ReadOnlySpan<byte> senders, ArkivApply apply)
    {
        fixed (byte* b = blockRlp)
        fixed (byte* s = senders)
        {
            int status = arkiv_execute_block(handle, b, (nuint)blockRlp.Length, s, (nuint)senders.Length, (int)apply, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Commits a block held by <see cref="ExecuteBlock"/>; <see cref="ArkivStatus.NotOnHead"/> when it is not held over the head.</summary>
    public static void Adopt(ulong handle, ReadOnlySpan<byte> blockHash)
    {
        fixed (byte* h = blockHash)
        {
            int status = arkiv_adopt(handle, h, out byte* ptr, out nuint len);
            Take(status, ptr, len);
        }
    }

    public static byte[] Account(ulong handle, ReadOnlySpan<byte> address)
    {
        fixed (byte* a = address)
        {
            int status = arkiv_account(handle, a, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    public static byte[] Call(ulong handle, ReadOnlySpan<byte> requestJson)
    {
        fixed (byte* r = requestJson)
        {
            int status = arkiv_call(handle, r, (nuint)requestJson.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    public static byte[] Rpc(ulong handle, string method, string parametersJson, ulong headNumber)
    {
        byte[] m = Encoding.UTF8.GetBytes(method);
        byte[] p = Encoding.UTF8.GetBytes(parametersJson);
        fixed (byte* mp = m)
        fixed (byte* pp = p)
        {
            int status = arkiv_rpc(handle, mp, (nuint)m.Length, pp, (nuint)p.Length, headNumber, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    /// <summary>Every account of the GolemDB store in <paramref name="directory"/>, which must not be open: 20 bytes each.</summary>
    public static byte[] StoreAccounts(string directory)
    {
        byte[] dir = Encoding.UTF8.GetBytes(directory);
        fixed (byte* d = dir)
        {
            int status = arkiv_store_accounts(d, (nuint)dir.Length, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    public static byte[] PurgeKeys(ulong handle, ulong block)
    {
        int status = arkiv_purge_keys(handle, block, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }

    public static ulong BuildBegin(ulong handle, ReadOnlySpan<byte> headerRlp)
    {
        fixed (byte* h = headerRlp)
        {
            int status = arkiv_build_begin(handle, h, (nuint)headerRlp.Length, out byte* ptr, out nuint len);
            return BinaryPrimitives.ReadUInt64LittleEndian(Take(status, ptr, len));
        }
    }

    public static byte[] BuildApply(ulong session, ReadOnlySpan<byte> tx, ReadOnlySpan<byte> sender)
    {
        fixed (byte* t = tx)
        fixed (byte* s = sender)
        {
            int status = arkiv_build_apply(session, t, (nuint)tx.Length, s, out byte* ptr, out nuint len);
            return Take(status, ptr, len);
        }
    }

    public static byte[] BuildFinish(ulong session)
    {
        int status = arkiv_build_finish(session, out byte* ptr, out nuint len);
        return Take(status, ptr, len);
    }
}
