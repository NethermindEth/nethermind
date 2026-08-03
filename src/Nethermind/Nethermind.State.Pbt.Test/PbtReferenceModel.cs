// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

internal static class PbtReferenceModel
{
    public static void SetAccount(Dictionary<string, byte[]> model, Address address, ulong nonce, in UInt256 balance, byte[]? code = null)
    {
        byte[] basicData = new byte[32];
        PbtKeyDerivation.PackBasicData(basicData, (uint)(code?.Length ?? 0), nonce, balance);
        Set(model, PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey), basicData);

        ValueHash256 codeHash = code is null or [] ? Keccak.OfAnEmptyString.ValueHash256 : ValueKeccak.Compute(code);
        Set(model, PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey), codeHash.ToByteArray());

        if (code is not { Length: > 0 }) return;
        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int chunkCount = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        for (int i = 0; i < chunkCount; i++)
        {
            PbtFullKey key = i < PbtKeyDerivation.HeaderCodeChunks
                ? PbtStateKey.Code(address, codeHash, i)
                : Eip8297KeyDerivation.OverflowCodeKey(codeHash.Bytes, i);
            Set(model, key, Chunk(chunks, i));
        }
    }

    public static void SetSlot(Dictionary<string, byte[]> model, Address address, in UInt256 slot, in UInt256 value)
    {
        byte[] value32 = new byte[32];
        value.ToBigEndian(value32);
        Set(model, PbtStateKey.Storage(address, slot), value32);
    }

    public static ValueHash256 Root(Dictionary<string, byte[]> model)
    {
        CurrentEipReferenceTree reference = new();
        foreach ((string key, byte[] value) in model) reference.Insert(Bytes.FromHexString(key), value);
        return new ValueHash256(reference.Merkelize());
    }

    private static byte[] Chunk(byte[] chunks, int chunkId) =>
        chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize).ToArray();

    private static void Set(Dictionary<string, byte[]> model, PbtFullKey key, byte[] value)
    {
        string encoded = key.Bytes.ToArray().ToHexString();
        if (value.AsSpan().IsZero()) model.Remove(encoded);
        else model[encoded] = value;
    }
}
