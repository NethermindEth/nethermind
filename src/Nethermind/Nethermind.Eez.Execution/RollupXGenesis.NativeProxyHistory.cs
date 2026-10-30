// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution;

public static partial class RollupXGenesis
{
    private static void EnsureNativeProxyHistory(JsonElement genesis)
    {
        JsonElement proxy = NativeHistoryAccount(genesis, EezConstants.L1HistoryAddress, 2, NativeProxyHistory.RuntimeHash);
        RequireNativeHistoryStorage(proxy, new Dictionary<UInt256, UInt256>(2)
        {
            [NativeProxyHistory.ImplementationSlot] = new UInt256(NativeProxyHistory.ImplementationAddress.Bytes, isBigEndian: true),
            [NativeProxyHistory.AdminSlot] = new UInt256(NativeProxyHistory.AdminAddress.Bytes, isBigEndian: true),
        });
        JsonElement implementation = NativeHistoryAccount(genesis, NativeProxyHistory.ImplementationAddress, 1, NativeProxyHistory.ImplementationHash);
        if (implementation.TryGetProperty("storage", out JsonElement slots) && slots.ValueKind != JsonValueKind.Null &&
            (slots.ValueKind != JsonValueKind.Object || slots.GetPropertyCount() != 0))
            throw Invalid("native history implementation must have empty storage");
        JsonElement admin = NativeHistoryAccount(genesis, NativeProxyHistory.AdminAddress, 1, NativeProxyHistory.AdminHash);
        JsonElement storage = Required(admin, "storage");
        if (storage.ValueKind != JsonValueKind.Object || storage.GetPropertyCount() != 1)
            throw Invalid("native history admin must contain only its upgrade owner");
        foreach (JsonProperty entry in storage.EnumerateObject())
        {
            UInt256 key = NativeChainDocument.StorageWord(entry.Name);
            UInt256 owner = NativeChainDocument.StorageWord(entry.Value.GetString());
            if (!key.IsZero || owner.IsZero || !(owner >> 160).IsZero || owner == new UInt256(Address.SystemUser.Bytes, isBigEndian: true))
                throw Invalid("native history upgrade owner is invalid");
        }
    }

    private static JsonElement NativeHistoryAccount(JsonElement genesis, Address address, ulong nonce, ValueHash256 runtime)
    {
        JsonElement account = FindAccount(genesis, address) ?? throw Invalid("missing native history account");
        if (Quantity(account, "nonce") != nonce || Quantity(account, "balance") != 0 ||
            ValueKeccak.Compute(Bytes.FromHexString(Required(account, "code").GetString()!)) != runtime)
            throw Invalid("native history code, nonce or balance differs from the profile");
        return account;
    }

    private static void RequireNativeHistoryStorage(JsonElement account, Dictionary<UInt256, UInt256> expected)
    {
        JsonElement storage = Required(account, "storage");
        if (storage.ValueKind != JsonValueKind.Object || storage.GetPropertyCount() != expected.Count)
            throw Invalid("native history genesis storage differs from the profile");
        foreach (JsonProperty entry in storage.EnumerateObject())
        {
            UInt256 key = NativeChainDocument.StorageWord(entry.Name);
            UInt256 value = NativeChainDocument.StorageWord(entry.Value.GetString());
            if (!expected.Remove(key, out UInt256 wanted) || value != wanted)
                throw Invalid("native history genesis storage differs from the profile");
        }
    }
}
