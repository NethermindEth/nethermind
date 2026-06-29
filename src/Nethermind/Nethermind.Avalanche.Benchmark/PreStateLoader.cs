// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>
/// Seeds an <see cref="IWorldState"/> from a JSON pre-state file so that the benchmarked blocks have
/// the accounts/code/storage their transactions touch.
/// </summary>
/// <remarks>
/// The pre-state JSON is a flat <c>address -&gt; account</c> map (the same shape produced by
/// <c>debug_dumpBlock</c> / geth state dumps and Ethereum Foundation test "pre" sections):
/// <code>
/// {
///   "0x...": { "balance": "0x..", "nonce": "0x..", "code": "0x..", "storage": { "0xkey": "0xval" } }
/// }
/// </code>
/// All scalar fields are optional and accept <c>0x</c>-prefixed hex. <c>balance</c>/<c>nonce</c> default
/// to zero, <c>code</c> to empty, and <c>storage</c> to none. Without a pre-state the harness still runs
/// against an empty state — useful only for a self-contained range whose first block creates everything
/// it later reads.
/// </remarks>
public static class PreStateLoader
{
    private sealed class AccountState
    {
        [JsonPropertyName("balance")] public string? Balance { get; set; }
        [JsonPropertyName("nonce")] public string? Nonce { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("storage")] public Dictionary<string, string>? Storage { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Applies the accounts declared in <paramref name="preStatePath"/> to <paramref name="state"/>.
    /// The caller is responsible for opening the world-state scope and committing afterwards.
    /// </summary>
    /// <returns>The number of accounts seeded.</returns>
    public static int Apply(IWorldState state, string preStatePath, IReleaseSpec spec)
    {
        if (!File.Exists(preStatePath))
        {
            throw new FileNotFoundException($"Pre-state file not found at '{preStatePath}'.", preStatePath);
        }

        Dictionary<string, AccountState>? accounts;
        using (FileStream stream = File.OpenRead(preStatePath))
        {
            accounts = JsonSerializer.Deserialize<Dictionary<string, AccountState>>(stream, Options);
        }

        if (accounts is null)
        {
            throw new InvalidOperationException($"Pre-state file '{preStatePath}' did not deserialize to an account map.");
        }

        foreach ((string addressHex, AccountState account) in accounts)
        {
            Address address = new(addressHex);
            UInt256 balance = ParseUInt256(account.Balance);
            ulong nonce = (ulong)ParseUInt256(account.Nonce);

            state.CreateAccount(address, balance, nonce);

            if (!string.IsNullOrWhiteSpace(account.Code))
            {
                byte[] code = Bytes.FromHexString(account.Code);
                if (code.Length > 0)
                {
                    state.InsertCode(address, code, spec, isGenesis: true);
                }
            }

            if (account.Storage is { Count: > 0 })
            {
                foreach ((string keyHex, string valueHex) in account.Storage)
                {
                    UInt256 key = ParseUInt256(keyHex);
                    // World state stores storage values as the big-endian bytes with leading zeros trimmed.
                    byte[] value = Bytes.FromHexString(valueHex).WithoutLeadingZeros().ToArray();
                    state.Set(new StorageCell(address, key), value);
                }
            }
        }

        return accounts.Count;
    }

    private static UInt256 ParseUInt256(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return UInt256.Zero;
        }

        return Bytes.FromHexString(hex).ToUInt256();
    }
}
