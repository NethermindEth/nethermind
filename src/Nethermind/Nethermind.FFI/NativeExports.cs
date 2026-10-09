// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.FFI;

/// <summary>Entry points resolved by the native shim through <c>hdt_get_function_pointer</c>.</summary>
/// <remarks>
/// Each export catches everything: an exception escaping an <see cref="UnmanagedCallersOnlyAttribute"/> method
/// terminates the process. Layouts of the <c>Nm*</c> structs must match
/// <c>native/nethermind_ffi.h</c>.
/// </remarks>
public static unsafe class NativeExports
{
    private static readonly HeaderDecoder HeaderDecoder = new();

    /// <param name="config">UTF-8 config name or path.</param>
    /// <param name="overrides">UTF-8 <c>Category.Name=value</c> entries.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Start(byte* config, byte** overrides, int overrideCount)
    {
        try
        {
            Dictionary<string, string> configOverrides = [];
            for (int i = 0; i < overrideCount; i++)
            {
                string entry = Marshal.PtrToStringUTF8((nint)overrides[i])!;
                int separator = entry.IndexOf('=');
                configOverrides[entry[..separator]] = entry[(separator + 1)..];
            }

            FfiHost.Start(Marshal.PtrToStringUTF8((nint)config)!, configOverrides);
            return (int)FfiStatus.Ok;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WaitReady(int timeoutMs)
    {
        try
        {
            return (int)FfiHost.WaitReady(timeoutMs);
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetHead(NmHead* head)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                if (node.Head is not { } header) return FfiStatus.NotReady;

                head->Number = header.Number;
                CopyHash(header.Hash, head->Hash);
                CopyHash(header.StateRoot, head->StateRoot);
                return FfiStatus.Ok;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    /// <param name="blockHash">32 bytes, or null for the head.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetHeader(byte* blockHash, NmBuffer* headerRlp)
    {
        *headerRlp = default;
        try
        {
            return (int)FfiHost.Use(node =>
            {
                BlockHeader? header = blockHash is null ? node.Head : node.FindHeader(ReadHash(blockHash));
                if (header is null) return blockHash is null ? FfiStatus.NotReady : FfiStatus.NotFound;

                headerRlp->Data = Allocate(HeaderDecoder.Encode(header).Bytes, out headerRlp->Length);
                return FfiStatus.Ok;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetChainConfig(NmBuffer* genesisJson)
    {
        *genesisJson = default;
        try
        {
            return (int)FfiHost.Use(node =>
            {
                genesisJson->Data = Allocate(node.GetChainConfig(), out genesisJson->Length);
                return FfiStatus.Ok;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetCode(byte* codeHash, NmBuffer* code)
    {
        *code = default;
        try
        {
            return (int)FfiHost.Use(node =>
            {
                if (node.GetCode(new ValueHash256(new ReadOnlySpan<byte>(codeHash, Hash256.Size))) is not { } bytes) return FfiStatus.NotFound;

                code->Data = Allocate(bytes, out code->Length);
                return FfiStatus.Ok;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    /// <param name="state">Receives a handle to the block's header, released by <see cref="CloseState"/>.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int OpenState(byte* blockHash, nint* state)
    {
        *state = 0;
        try
        {
            return (int)FfiHost.Use(node =>
            {
                FfiStatus status = node.OpenState(ReadHash(blockHash), out BlockHeader? header);
                if (status is FfiStatus.Ok) *state = GCHandle.ToIntPtr(GCHandle.Alloc(header));
                return status;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void CloseState(nint state)
    {
        if (state != 0) GCHandle.FromIntPtr(state).Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAccount(nint state, byte* address, NmAccount* account)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                if (!node.TryGetAccount(StateHeader(state), ReadAddress(address), out AccountStruct value)) return FfiStatus.NotFound;

                account->Nonce = value.Nonce;
                value.Balance.ToBigEndian(new Span<byte>(account->Balance, 32));
                value.CodeHash.Bytes.CopyTo(new Span<byte>(account->CodeHash, Hash256.Size));
                return FfiStatus.Ok;
            });
        }
        catch (MissingTrieNodeException)
        {
            return (int)FfiStatus.StateUnavailable;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetStorage(nint state, byte* address, byte* slot, byte* value)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                UInt256 index = new(new ReadOnlySpan<byte>(slot, 32), isBigEndian: true);
                node.GetStorage(StateHeader(state), ReadAddress(address), index).ToBigEndian(new Span<byte>(value, 32));
                return FfiStatus.Ok;
            });
        }
        catch (MissingTrieNodeException)
        {
            return (int)FfiStatus.StateUnavailable;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetBlockHash(nint state, ulong number, byte* hash)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                if (node.GetBlockHash(StateHeader(state), number) is not { } blockHash) return FfiStatus.NotFound;

                CopyHash(blockHash, hash);
                return FfiStatus.Ok;
            });
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ExecuteBlock(byte* blockRlp, nuint length, NmBlockResult* result)
    {
        *result = default;
        try
        {
            return (int)FfiHost.Use(node =>
            {
                BlockExecutionResult execution = node.ExecuteBlock(new ReadOnlySpan<byte>(blockRlp, checked((int)length)));
                if (execution.Header is { } header)
                {
                    result->GasUsed = header.GasUsed;
                    CopyHash(header.StateRoot, result->StateRoot);
                    CopyHash(header.ReceiptsRoot, result->ReceiptsRoot);
                    CopyHash(header.Hash, result->BlockHash);
                    result->ReceiptsRlp = Allocate(execution.ReceiptsRlp, out result->ReceiptsLength);
                }

                SetError(result, execution.Error);
                return execution.Status;
            });
        }
        catch (Exception e)
        {
            SetError(result, e.ToString());
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Free(void* pointer) => NativeMemory.Free(pointer);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int SetTxCallback(delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, nuint, void> callback, nint userData)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                node.SetTxCallback(callback, userData);
                return FfiStatus.Ok;
            });
        }
        catch (InvalidOperationException)
        {
            return (int)FfiStatus.CallbackAlreadySet;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int SetNodeCallback(delegate* unmanaged[Cdecl]<nint, NmNodeStatus*, void> callback, nint userData)
    {
        try
        {
            return (int)FfiHost.Use(node =>
            {
                node.SetNodeCallback(callback, userData);
                return FfiStatus.Ok;
            });
        }
        catch (InvalidOperationException)
        {
            return (int)FfiStatus.CallbackAlreadySet;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Stop(int exitCode)
    {
        try
        {
            FfiHost.Stop(exitCode);
            return (int)FfiStatus.Ok;
        }
        catch
        {
            return (int)FfiStatus.InternalError;
        }
    }

    /// <summary>Waits for the node to stop and returns its exit code.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Join()
    {
        try
        {
            return FfiHost.Join();
        }
        catch
        {
            return ExitCodes.GeneralError;
        }
    }

    internal static void CopyHash(Hash256? hash, byte* destination) =>
        (hash ?? Hash256.Zero).Bytes.CopyTo(new Span<byte>(destination, Hash256.Size));

    private static Hash256 ReadHash(byte* hash) => new(new ReadOnlySpan<byte>(hash, Hash256.Size));

    private static Address ReadAddress(byte* address) => new(new ReadOnlySpan<byte>(address, Address.Size));

    private static BlockHeader StateHeader(nint state) => (BlockHeader)GCHandle.FromIntPtr(state).Target!;

    private static void SetError(NmBlockResult* result, string? error)
    {
        NativeMemory.Free(result->Error);
        result->Error = error is null ? null : Allocate(Encoding.UTF8.GetBytes(error), out result->ErrorLength);
    }

    private static byte* Allocate(ReadOnlySpan<byte> source, out nuint length)
    {
        length = (nuint)source.Length;
        byte* destination = (byte*)NativeMemory.Alloc(length);
        source.CopyTo(new Span<byte>(destination, source.Length));
        return destination;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmHead
{
    public ulong Number;
    public fixed byte Hash[32];
    public fixed byte StateRoot[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmBlockResult
{
    public ulong GasUsed;
    public fixed byte StateRoot[32];
    public fixed byte ReceiptsRoot[32];
    public fixed byte BlockHash[32];
    public byte* ReceiptsRlp;
    public nuint ReceiptsLength;
    public byte* Error;
    public nuint ErrorLength;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmBuffer
{
    public byte* Data;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmAccount
{
    public ulong Nonce;
    public fixed byte Balance[32];
    public fixed byte CodeHash[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmNodeStatus
{
    public int Event;
    public ulong Number;
    public fixed byte Hash[32];
    public fixed byte StateRoot[32];
}
