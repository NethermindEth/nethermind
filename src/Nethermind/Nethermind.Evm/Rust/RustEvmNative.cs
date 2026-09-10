// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if RUST_EVM
using System;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.Rust;

/// <summary>
/// The C ABI of the Rust interpreter (crate <c>nm-ffi</c>), type for type.
/// </summary>
/// <remarks>
/// Every structure is sequential and made of integers and pointers, so the runtime passes it as
/// it is. The library name is the one thing that differs between a host, where the interpreter
/// is a shared library beside the executable, and a zkVM guest, where it is linked in; the two
/// <c>.std.cs</c> / <c>.zkevm.cs</c> halves of this class supply it.
/// </remarks>
internal static unsafe partial class RustEvmNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FfiU256
    {
        public ulong L0, L1, L2, L3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FfiAddress
    {
        public fixed byte Bytes[20];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FfiHash
    {
        public fixed byte Bytes[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiBytes
    {
        public byte* Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiAccount
    {
        public ulong Nonce;
        public FfiU256 Balance;
        public FfiHash CodeHash;
        public FfiHash StorageRoot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiStorageCell
    {
        public FfiAddress Address;
        public FfiU256 Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiCallbacks
    {
        public void* Context;
        public delegate* unmanaged<void*, FfiAddress*, FfiAccount*, int> Account;
        public delegate* unmanaged<void*, FfiAddress*, FfiU256*, FfiU256*, int> Storage;
        public delegate* unmanaged<void*, FfiHash*, FfiBytes*, int> Code;
        public delegate* unmanaged<void*, ulong, FfiHash*, int> BlockHash;
        public delegate* unmanaged<void*, FfiAddress*, void> AccountRead;
        public delegate* unmanaged<void*, FfiAddress*, void> BytecodeAccess;
        public delegate* unmanaged<void*, FfiAddress*, void> AccountAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiBlock
    {
        public ulong Number;
        public ulong Timestamp;
        public ulong GasLimit;
        public FfiAddress Beneficiary;
        public FfiU256 BaseFeePerGas;
        public FfiHash PrevRandao;
        public ulong ExcessBlobGas;
        public byte HasExcessBlobGas;
        public ulong SlotNumber;
        public byte HasSlotNumber;
        public FfiU256 BlobBaseFee;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiTx
    {
        public FfiAddress Origin;
        public FfiU256 GasPrice;
        public FfiHash* BlobVersionedHashes;
        public nuint BlobVersionedHashCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiGas
    {
        public ulong Remaining;
        public long StateReservoir;
        public long StateGasUsed;
        public long StateGasSpill;
        public long StateGasSpillRefunded;
        public byte OutOfGas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiFrame
    {
        public FfiGas Gas;
        public byte ExecutionType;
        public byte IsStatic;
        public byte IsCreateStateGasCharged;
        public FfiAddress ExecutingAccount;
        public FfiAddress Caller;
        public FfiAddress CodeSource;
        public byte HasCodeSource;
        public FfiBytes Code;
        public byte CodeIsPrecompile;
        public FfiU256 Value;
        public FfiBytes Input;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiWarm
    {
        public FfiAddress* Addresses;
        public nuint AddressCount;
        public FfiStorageCell* Cells;
        public nuint CellCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiRequest
    {
        public byte Fork;
        public byte Gnosis;
        public FfiU256 ChainId;
        public FfiBlock Block;
        public FfiTx Tx;
        public FfiFrame Frame;
        public FfiWarm Warm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiLog
    {
        public FfiAddress Address;
        public FfiHash* Topics;
        public nuint TopicCount;
        public FfiBytes Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiAccountChange
    {
        public FfiAddress Address;
        public byte Exists;
        public ulong Nonce;
        public FfiU256 Balance;
        public FfiHash CodeHash;
        public FfiBytes Code;
        public byte HasCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiStorageChange
    {
        public FfiAddress Address;
        public FfiU256 Key;
        public FfiU256 Value;
    }

    public const int StatusOk = 0;
    public const int StatusReadFailed = -1;
    public const int StatusUnportedPrecompile = -2;
    public const int StatusPanicked = -3;
    public const int StatusUnknownFork = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct FfiResult
    {
        public int Status;
        public int Exception;
        public byte ShouldRevert;
        public byte RestoreRipemdTouch;
        public FfiGas Gas;
        public long Refund;
        public FfiBytes Output;
        public FfiLog* Logs;
        public nuint LogCount;
        public FfiAddress* Destroy;
        public nuint DestroyCount;
        public FfiAccountChange* Accounts;
        public nuint AccountCount;
        public FfiStorageChange* Storage;
        public nuint StorageCount;
        public FfiAddress* Wiped;
        public nuint WipedCount;
        public FfiAddress* TouchedEmpty;
        public nuint TouchedEmptyCount;
        public FfiAddress UnportedPrecompile;
        public void* Owner;
    }

    [DllImport(Lib, EntryPoint = "nm_evm_abi_version", ExactSpelling = true)]
    public static extern uint AbiVersion();

    [DllImport(Lib, EntryPoint = "nm_evm_init", ExactSpelling = true)]
    public static extern void Init();

    [DllImport(Lib, EntryPoint = "nm_evm_execute", ExactSpelling = true)]
    public static extern int Execute(FfiRequest* request, FfiCallbacks* callbacks, FfiResult* result);

    [DllImport(Lib, EntryPoint = "nm_evm_free", ExactSpelling = true)]
    public static extern void Free(FfiResult* result);

    /// <summary>The ABI this binding was written against.</summary>
    public const uint ExpectedAbiVersion = 2;
}
#endif
