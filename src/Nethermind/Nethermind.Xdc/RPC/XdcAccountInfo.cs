// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc.RPC;

/// <summary>Account summary in the shape XDPoSChain reports it.</summary>
/// <remarks>
/// Carries the code's hash and length rather than the code itself, so querying a large contract does not
/// ship its bytecode. An account with no entry in the state trie is reported with zero hashes rather than
/// the empty-code and empty-trie hashes, matching the reference and letting a caller tell an absent
/// account apart from one that exists with neither code nor storage.
/// </remarks>
public class XdcAccountInfo
{
    /// <summary>The account queried, echoed back.</summary>
    public Address? Address { get; set; }

    public UInt256 Balance { get; set; }

    public ulong Nonce { get; set; }

    /// <summary>Hash of the account's code, or zero when the account does not exist.</summary>
    public Hash256? CodeHash { get; set; }

    /// <summary>Length of the account's code in bytes; zero for an account that holds none.</summary>
    public long CodeSize { get; set; }

    /// <summary>Root of the account's storage trie, or zero when the account does not exist.</summary>
    public Hash256? StorageHash { get; set; }

    /// <summary>Builds the all-zero report the reference returns for an address with no account.</summary>
    public static XdcAccountInfo Absent(Address address) => new()
    {
        Address = address,
        CodeHash = Hash256.Zero,
        StorageHash = Hash256.Zero,
    };
}
