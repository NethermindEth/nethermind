// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured <see cref="BlockHeader"/> carrying the <c>step</c> + <c>signature</c>
/// seal that AuRa chains write in place of the Ethash <c>mixHash</c> + <c>nonce</c> pair.
/// </summary>
/// <remarks>
/// Implements <see cref="IHashResolver"/> so <see cref="BlockHeaderExtensions.CalculateValueHash"/>
/// dispatches the hash computation through <see cref="AuRaHeaderDecoder"/> — the base
/// <c>HeaderDecoder</c> no longer needs to know AuRa exists.
/// </remarks>
public sealed class AuRaBlockHeader(
    Hash256? parentHash,
    Hash256? unclesHash,
    Address? beneficiary,
    in UInt256 difficulty,
    long number,
    long gasLimit,
    ulong timestamp,
    byte[] extraData)
    : BlockHeader(parentHash!, unclesHash!, beneficiary!, in difficulty, number, gasLimit, timestamp, extraData),
      IAuRaSealedHeader, IHashResolver
{
    private static readonly AuRaHeaderDecoder s_decoder = new();

    /// <inheritdoc/>
    public long? AuRaStep { get; set; }

    /// <inheritdoc/>
    public byte[]? AuRaSignature { get; set; }

    public ValueHash256 CalculateHash(RlpBehaviors behaviors = RlpBehaviors.None)
    {
        KeccakRlpStream stream = new();
        s_decoder.Encode(stream, this, behaviors);
        return stream.GetValueHash();
    }
}
