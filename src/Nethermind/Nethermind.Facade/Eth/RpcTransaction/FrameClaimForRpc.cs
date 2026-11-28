// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>JSON-RPC view of an EIP-8141 claim: <c>[verifier, key, msg, gas_limit, witness_len]</c>.</summary>
public class FrameClaimForRpc
{
    /// <summary>A protocol verifier address, an active precompile, or a non-delegated contract.</summary>
    public Address Verifier { get; set; } = Address.Zero;

    /// <summary>Empty for the transaction sender, or a non-zero 20-byte or 32-byte key.</summary>
    public ReadOnlyMemory<byte> Key { get; set; }

    /// <summary>The digest the claim is over, or empty when it is over the transaction's canonical signature hash.</summary>
    /// <remarks>Accepted only as empty or exactly 32 non-zero bytes.</remarks>
    public ReadOnlyMemory<byte> Msg { get; set; }

    /// <summary>The execution gas a custom verifier is given, charged in full; zero for a protocol verifier.</summary>
    public ulong GasLimit { get; set; }

    /// <summary>The exact witness length a custom verifier takes; zero for a protocol verifier.</summary>
    [JsonPropertyName("witnessLen")]
    public ulong WitnessLength { get; set; }

    [JsonConstructor]
    public FrameClaimForRpc() { }

    public FrameClaimForRpc(TxFrameClaim claim)
    {
        Verifier = claim.Verifier;
        Key = claim.Key;
        Msg = claim.Msg;
        GasLimit = claim.GasLimit;
        WitnessLength = claim.WitnessLength;
    }

    public TxFrameClaim ToClaim() => new(Verifier, Key, Msg, GasLimit, WitnessLength);

    public static FrameClaimForRpc[]? FromClaims(TxFrameClaim[]? claims)
    {
        if (claims is null) return null;

        FrameClaimForRpc[] result = new FrameClaimForRpc[claims.Length];
        for (int i = 0; i < claims.Length; i++)
        {
            result[i] = new FrameClaimForRpc(claims[i]);
        }

        return result;
    }

    /// <summary>Maps the deserialized <c>claims</c> list onto the transaction's claims.</summary>
    /// <param name="claims">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="claims"/> is absent.</param>
    /// <returns><c>false</c> if any element was JSON <c>null</c>.</returns>
    public static bool TryToClaims(FrameClaimForRpc[]? claims, out TxFrameClaim[]? converted) =>
        RpcListConverter.TryConvert(claims, static c => c.ToClaim(), out converted);
}
