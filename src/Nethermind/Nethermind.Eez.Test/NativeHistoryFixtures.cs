// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Eez.Test;

internal static class NativeHistoryFixtures
{
    internal static void InstallProxy(IWorldState state)
    {
        JsonElement alloc = StatelessFixtures.ReadJson("rollupx/native-profile-v6", "genesis.json").GetProperty("alloc");
        foreach (Address address in new[] { EezConstants.L1HistoryAddress, NativeProxyHistory.ImplementationAddress, NativeProxyHistory.AdminAddress })
        {
            state.CreateAccount(address, 0, address == EezConstants.L1HistoryAddress ? 2UL : 1UL);
            JsonElement account = alloc.GetProperty(address.ToString(false, false));
            state.InsertCode(address, Bytes.FromHexString(account.GetProperty("code").GetString()!), Osaka.Instance);
            if (account.TryGetProperty("storage", out JsonElement storage))
                foreach (JsonProperty entry in storage.EnumerateObject())
                    state.Set(new StorageCell(address, new UInt256(Bytes.FromHexString(entry.Name), isBigEndian: true)), new UInt256(Bytes.FromHexString(entry.Value.GetString()!), isBigEndian: true));
        }
    }
}
