// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Subprotocols.Eth.V75;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary><c>eth/76</c> proof wrapper manifest messages, after the <c>eth/75</c> wrapper messages; the protocol version and
/// message IDs are provisional.</summary>
public static class Eth76MessageCode
{
    public const int GetProofWrapperManifests = 0x1b;
    public const int ProofWrapperManifests = 0x1c;

    /// <summary>Whether <paramref name="code"/> is one of the five wrapper messages.</summary>
    public static bool IsWrapperMessage(int code) => code is >= Eth75MessageCode.NewProofWrapperHashes and <= ProofWrapperManifests;
}

/// <summary><c>eth/76</c> message bounds and this client's local message caps.</summary>
public static class Eth76Protocol
{
    /// <summary>One past the last message ID, <c>ProofWrapperManifests</c>.</summary>
    public const int MessageIdSpaceSize = Eth76MessageCode.ProofWrapperManifests + 1;

    /// <summary>The largest NewProofWrapperHashes accepted, 64 <c>[mode, length, hash, deps-hash, txs-hash, txs-meta]</c> entries
    /// with 17-transaction metadata at the widest wire integers and three fees each, bounding message data before decompression
    /// (check_vectors.py).</summary>
    /// <remarks>A local cap: fee lists have no fixed length, but an EIP-8141 transaction carries two fees.</remarks>
    public const int MaxNewProofWrapperHashesBytes = 201_732;

    /// <summary>The largest valid GetProofWrappers, 32 hashes under the widest request ID and the compact flag, bounding message
    /// data before decompression (check_vectors.py).</summary>
    public const int MaxGetProofWrappersBytes = 1072;

    /// <summary>The largest valid GetProofWrapperManifests, 32 hashes under the widest request ID.</summary>
    public const int MaxGetProofWrapperManifestsBytes = Eth75Protocol.MaxGetProofWrappersBytes;

    /// <summary>The largest ProofWrapperManifests accepted, checked before decompression.</summary>
    /// <remarks>A local cap. A responder stops adding manifests once the response reaches the soft limit, so the last one may
    /// take it past the limit; a manifest of a wrapper within <c>MAX_WRAPPER_BYTES</c> describes transactions of at least a few
    /// dozen bytes each with under two hundred bytes of metadata, so it is allowed several wrappers' worth.</remarks>
    public const int LocalMaxManifestsBytes = Eth75Protocol.SoftResponseLimit + 4 * Eth75Protocol.MaxWrapperBytes;

    /// <summary>Whether an advertised uncompressed size exceeds this client's cap for an <c>eth/76</c> wrapper message, checked
    /// before decompression.</summary>
    public static bool ExceedsMessageLimit(int code, int uncompressedLength) => code switch
    {
        Eth75MessageCode.NewProofWrapperHashes => uncompressedLength > MaxNewProofWrapperHashesBytes,
        Eth75MessageCode.GetProofWrappers => uncompressedLength > MaxGetProofWrappersBytes,
        Eth76MessageCode.GetProofWrapperManifests => uncompressedLength > MaxGetProofWrapperManifestsBytes,
        Eth76MessageCode.ProofWrapperManifests => uncompressedLength > LocalMaxManifestsBytes,
        _ => Eth75Protocol.ExceedsMessageLimit(code, uncompressedLength)
    };
}
