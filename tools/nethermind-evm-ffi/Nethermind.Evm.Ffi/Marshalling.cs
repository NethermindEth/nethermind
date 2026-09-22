// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Ffi;

/// <summary>
/// Copies one transaction's outcome into a single unmanaged block the host frees in one call.
/// </summary>
/// <remarks>
/// One allocation rather than one per array keeps the free side trivial and the boundary cheap:
/// the arrays sit at the front in declaration order and every variable-length payload — output,
/// deployed code, log topics and log data — follows in one blob that the struct fields point into.
/// </remarks>
internal static unsafe class Marshalling
{
    public static void Write(Engine engine, NmEvmResult* result)
    {
        ResultTracer tracer = engine.Tracer;
        Diff diff = engine.Diff;

        int accountCount = diff.Accounts.Count;
        int storageCount = diff.Storage.Count;
        int codeCount = diff.Code.Count;
        int logCount = tracer.Logs.Length;

        long blobBytes = tracer.Output.Length;
        for (int i = 0; i < codeCount; i++) blobBytes += diff.Code[i].Code.Length;
        for (int i = 0; i < logCount; i++)
        {
            blobBytes += (long)tracer.Logs[i].Topics.Length * 32;
            blobBytes += tracer.Logs[i].Data.Length;
        }

        long offAccounts = 0;
        long offStorage = Align8(offAccounts + (long)accountCount * sizeof(NmAccountChange));
        long offCode = Align8(offStorage + (long)storageCount * sizeof(NmStorageChange));
        long offLogs = Align8(offCode + (long)codeCount * sizeof(NmCodeChange));
        long offBlob = Align8(offLogs + (long)logCount * sizeof(NmLog));
        long total = offBlob + blobBytes;

        // NativeMemory.Alloc(0) is legal but returns a pointer nothing points into; keep at least
        // one byte so a zero-change result still has an arena to free.
        byte* arena = (byte*)NativeMemory.Alloc((nuint)Math.Max(total, 1));

        NmAccountChange* accounts = (NmAccountChange*)(arena + offAccounts);
        NmStorageChange* storage = (NmStorageChange*)(arena + offStorage);
        NmCodeChange* code = (NmCodeChange*)(arena + offCode);
        NmLog* logs = (NmLog*)(arena + offLogs);
        byte* blob = arena + offBlob;

        for (int i = 0; i < accountCount; i++)
        {
            (Address address, Account? account) = diff.Accounts[i];
            NmAccountChange* slot = accounts + i;
            address.Bytes.CopyTo(new Span<byte>(slot->Address, 20));
            slot->Exists = account is null ? 0 : 1;
            slot->Nonce = account?.Nonce ?? 0;
            if (account is null)
            {
                new Span<byte>(slot->Balance, 32).Clear();
                new Span<byte>(slot->CodeHash, 32).Clear();
            }
            else
            {
                account.Balance.ToLittleEndian(new Span<byte>(slot->Balance, 32));
                account.CodeHash.Bytes.CopyTo(new Span<byte>(slot->CodeHash, 32));
            }
        }

        for (int i = 0; i < storageCount; i++)
        {
            (Address address, UInt256 key, UInt256 value) = diff.Storage[i];
            NmStorageChange* slot = storage + i;
            address.Bytes.CopyTo(new Span<byte>(slot->Address, 20));
            key.ToLittleEndian(new Span<byte>(slot->Key, 32));
            value.ToLittleEndian(new Span<byte>(slot->Value, 32));
        }

        byte* cursor = blob;
        byte* output = cursor;
        tracer.Output.CopyTo(new Span<byte>(cursor, tracer.Output.Length));
        cursor += tracer.Output.Length;

        for (int i = 0; i < codeCount; i++)
        {
            (ValueHash256 hash, byte[] bytes) = diff.Code[i];
            NmCodeChange* slot = code + i;
            hash.Bytes.CopyTo(new Span<byte>(slot->CodeHash, 32));
            bytes.CopyTo(new Span<byte>(cursor, bytes.Length));
            slot->Code = cursor;
            slot->CodeLen = bytes.Length;
            cursor += bytes.Length;
        }

        for (int i = 0; i < logCount; i++)
        {
            LogEntry entry = tracer.Logs[i];
            NmLog* slot = logs + i;
            entry.Address.Bytes.CopyTo(new Span<byte>(slot->Address, 20));

            slot->TopicCount = entry.Topics.Length;
            slot->Topics = cursor;
            for (int t = 0; t < entry.Topics.Length; t++)
            {
                entry.Topics[t].Bytes.CopyTo(new Span<byte>(cursor, 32));
                cursor += 32;
            }

            slot->Data = cursor;
            slot->DataLen = entry.Data.Length;
            entry.Data.CopyTo(new Span<byte>(cursor, entry.Data.Length));
            cursor += entry.Data.Length;
        }

        result->Success = tracer.Success ? 1 : 0;
        result->GasUsed = tracer.GasSpent;
        result->GasRefunded = tracer.GasRefunded;
        result->Output = output;
        result->OutputLen = tracer.Output.Length;
        result->Accounts = accounts;
        result->AccountCount = accountCount;
        result->Storage = storage;
        result->StorageCount = storageCount;
        result->Code = code;
        result->CodeCount = codeCount;
        result->Logs = logs;
        result->LogCount = logCount;
        result->Arena = arena;
    }

    private static long Align8(long value) => (value + 7) & ~7L;
}
