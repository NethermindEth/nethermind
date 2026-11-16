// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Processing;

internal static class ProcessedHeaderHash
{
    /// <summary>Returns the hash of <paramref name="processed"/>, which must equal the hash of <paramref name="suggested"/>
    /// for the block to be valid.</summary>
    /// <remarks>
    /// When every field that <see cref="HeaderDecoder"/> encodes is equal, the two headers have the same encoding, so the
    /// hash of <paramref name="suggested"/> is returned and the processed header is not encoded or hashed. This is
    /// correct only when the hash of <paramref name="suggested"/> is the hash of its fields. Any difference falls back to
    /// <see cref="BlockHeaderExtensions.CalculateHash(BlockHeader, RlpBehaviors)"/>, so a processed header that does not
    /// match still fails the block hash comparison. The fields are compared exactly, null included, which is stricter than
    /// the encoding.
    /// </remarks>
    public static Hash256 Calculate(BlockHeader processed, BlockHeader suggested) =>
        suggested.Hash is { } hash && HashedFieldsEqual(processed, suggested) ? hash : processed.CalculateHash();

    private static bool HashedFieldsEqual(BlockHeader a, BlockHeader b) =>
        // A header that resolves its own hash can encode more than the fields below.
        a is not IHashResolver && b is not IHashResolver
        && a.ParentHash == b.ParentHash
        && a.UnclesHash == b.UnclesHash
        && a.Beneficiary == b.Beneficiary
        && a.StateRoot == b.StateRoot
        && a.TxRoot == b.TxRoot
        && a.ReceiptsRoot == b.ReceiptsRoot
        && a.Bloom == b.Bloom
        && a.Difficulty == b.Difficulty
        && a.Number == b.Number
        && a.GasLimit == b.GasLimit
        && a.GasUsed == b.GasUsed
        && a.Timestamp == b.Timestamp
        && a.ExtraData.AsSpan().SequenceEqual(b.ExtraData)
        && a.MixHash == b.MixHash
        && a.Nonce == b.Nonce
        && a.BaseFeePerGas == b.BaseFeePerGas
        && a.WithdrawalsRoot == b.WithdrawalsRoot
        && a.BlobGasUsed == b.BlobGasUsed
        && a.ExcessBlobGas == b.ExcessBlobGas
        && a.ParentBeaconBlockRoot == b.ParentBeaconBlockRoot
        && a.RequestsHash == b.RequestsHash
        && a.BlockAccessListHash == b.BlockAccessListHash
        && a.SlotNumber == b.SlotNumber;
}
