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

/// <summary>
/// Maintains the expected logical tree content of integration scenarios as raw 32-byte key/value
/// entries (keyed by hex) and merkelizes them with the <see cref="EipReferenceTree"/> oracle.
/// Key derivation reuses the production <see cref="PbtKeyDerivation"/>, which is itself pinned by
/// <see cref="KeyDerivationTests"/>.
/// </summary>
internal static class PbtReferenceModel
{
    public static void SetAccount(Dictionary<string, byte[]> model, Address address, ulong nonce, in UInt256 balance, byte[]? code = null)
    {
        Stem headerStem = PbtKeyDerivation.AccountHeaderStem(address);
        byte[] basicData = new byte[32];
        PbtKeyDerivation.PackBasicData(basicData, (uint)(code?.Length ?? 0), nonce, balance);
        Set(model, headerStem, (byte)PbtKeyDerivation.BasicDataLeafKey, basicData);

        ValueHash256 codeHash = code is null or [] ? Keccak.OfAnEmptyString.ValueHash256 : ValueKeccak.Compute(code);
        Set(model, headerStem, (byte)PbtKeyDerivation.CodeHashLeafKey, codeHash.ToByteArray());

        if (code is { Length: > 0 })
        {
            byte[][] chunks = PbtKeyDerivation.ChunkifyCode(code);
            for (int i = 0; i < chunks.Length && i < PbtKeyDerivation.HeaderCodeChunks; i++)
            {
                Set(model, headerStem, PbtKeyDerivation.HeaderCodeChunkSubIndex(i), chunks[i]);
            }

            for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
            {
                Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(codeHash, i, out byte subIndex);
                Set(model, overflowStem, subIndex, chunks[i]);
            }
        }
    }

    public static void RemoveAccountHeader(Dictionary<string, byte[]> model, Address address)
    {
        string stemPrefix = PbtKeyDerivation.AccountHeaderStem(address).Bytes.ToHexString();
        List<string> keys = [];
        foreach (string key in model.Keys)
        {
            if (key.StartsWith(stemPrefix, StringComparison.Ordinal)) keys.Add(key);
        }

        foreach (string key in keys)
        {
            model.Remove(key);
        }
    }

    public static void SetSlot(Dictionary<string, byte[]> model, Address address, in UInt256 slot, in UInt256 value)
    {
        byte[] value32 = new byte[32];
        value.ToBigEndian(value32);

        byte subIndex;
        Stem stem;
        if (PbtKeyDerivation.IsHeaderSlot(slot))
        {
            stem = PbtKeyDerivation.AccountHeaderStem(address);
            subIndex = PbtKeyDerivation.HeaderSlotSubIndex(slot);
        }
        else
        {
            stem = PbtKeyDerivation.StorageStem(address, slot, out subIndex);
        }

        Set(model, stem, subIndex, value32);
    }

    public static ValueHash256 Root(Dictionary<string, byte[]> model)
    {
        EipReferenceTree reference = new();
        foreach ((string key, byte[] value) in model)
        {
            reference.Insert(Bytes.FromHexString(key), value);
        }

        return new ValueHash256(reference.Merkelize());
    }

    private static void Set(Dictionary<string, byte[]> model, in Stem stem, byte subIndex, byte[] value)
    {
        string key = ((byte[])[.. stem.Bytes, subIndex]).ToHexString();
        if (value.AsSpan().IsZero())
        {
            model.Remove(key);
        }
        else
        {
            model[key] = value;
        }
    }
}
