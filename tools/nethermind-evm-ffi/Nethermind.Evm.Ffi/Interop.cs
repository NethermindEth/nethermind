// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Evm.Ffi;

/// <summary>The four reads the host answers. Mirrors <c>NmEvmHost</c> in nethermind_evm.h.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmEvmHost
{
    public void* Ctx;
    public delegate* unmanaged<void*, byte*, ulong*, byte*, byte*, int> GetAccount;
    public delegate* unmanaged<void*, byte*, byte*, byte*, int> GetStorage;
    public delegate* unmanaged<void*, byte*, byte*, int, int> GetCode;
    public delegate* unmanaged<void*, ulong, byte*, int> GetBlockHash;
}

/// <summary>Mirrors <c>NmEvmBlock</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmEvmBlock
{
    public ulong Number;
    public ulong Timestamp;
    public ulong GasLimit;
    public fixed byte Coinbase[20];
    public fixed byte BaseFee[32];
    public fixed byte PrevRandao[32];
    public ulong ExcessBlobGas;
    public int HasExcessBlobGas;
}

/// <summary>Mirrors <c>NmEvmTx</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmEvmTx
{
    public byte TxType;
    public ulong Nonce;
    public ulong GasLimit;
    public fixed byte MaxFeePerGas[32];
    public fixed byte MaxPriorityFeePerGas[32];
    public fixed byte Value[32];
    public fixed byte Sender[20];
    public fixed byte To[20];
    public int HasTo;
    public byte* Data;
    public int DataLen;
}

/// <summary>Mirrors <c>NmAccountChange</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmAccountChange
{
    public fixed byte Address[20];
    public int Exists;
    public ulong Nonce;
    public fixed byte Balance[32];
    public fixed byte CodeHash[32];
}

/// <summary>Mirrors <c>NmStorageChange</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmStorageChange
{
    public fixed byte Address[20];
    public fixed byte Key[32];
    public fixed byte Value[32];
}

/// <summary>Mirrors <c>NmCodeChange</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmCodeChange
{
    public fixed byte CodeHash[32];
    public byte* Code;
    public int CodeLen;
}

/// <summary>Mirrors <c>NmLog</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmLog
{
    public fixed byte Address[20];
    public int TopicCount;
    public byte* Topics;
    public byte* Data;
    public int DataLen;
}

/// <summary>Mirrors <c>NmEvmResult</c>. Everything it points at lives in <see cref="Arena"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NmEvmResult
{
    public int Success;
    public ulong GasUsed;
    public ulong GasRefunded;

    public byte* Output;
    public int OutputLen;

    public NmAccountChange* Accounts;
    public int AccountCount;
    public NmStorageChange* Storage;
    public int StorageCount;
    public NmCodeChange* Code;
    public int CodeCount;
    public NmLog* Logs;
    public int LogCount;

    public void* Arena;
}

/// <summary>Status codes shared with the header.</summary>
public static class NmStatus
{
    public const int Ok = 0;
    public const int Argument = -1;
    public const int Engine = -2;
    public const int NoBlock = -3;
    public const int TxRejected = -4;
    public const int Internal = -5;
}
