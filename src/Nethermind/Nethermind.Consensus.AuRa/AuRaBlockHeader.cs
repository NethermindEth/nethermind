// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured <see cref="BlockHeader"/> carrying the <c>step</c> + <c>signature</c>
/// seal that AuRa chains write in place of the Ethash <c>mixHash</c> + <c>nonce</c> pair.
/// </summary>
/// <remarks>
/// Constructed by the <see cref="AuRaBlockHeaderHandler"/> bridge so that core code
/// (<c>ChainSpecLoader</c>, <c>HeaderDecoder</c>, test builders) can produce one without
/// taking a hard dependency on the AuRa plugin assembly.
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
    : BlockHeader(parentHash!, unclesHash!, beneficiary!, in difficulty, number, gasLimit, timestamp, extraData)
{
    /// <summary>AuRa step number (analogous to Ethash <c>nonce</c>).</summary>
    public long? AuRaStep { get; set; }

    /// <summary>AuRa signature over the header (analogous to Ethash <c>mixHash</c>).</summary>
    public byte[]? AuRaSignature { get; set; }
}
