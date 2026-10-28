// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 <c>eth/73</c> proof wrapper messages; the protocol version and message IDs are provisional.</summary>
public static class Eth73MessageCode
{
    public const int NewProofWrapperHashes = 0x16;
    public const int GetProofWrappers = 0x17;
    public const int ProofWrappers = 0x18;

    /// <summary>Whether <paramref name="code"/> is one of the three wrapper messages.</summary>
    public static bool IsWrapperMessage(int code) => code is >= NewProofWrapperHashes and <= ProofWrappers;
}

/// <summary>EIP-8437 message bounds and this client's local message caps.</summary>
public static class Eth73Protocol
{
    /// <summary>One past the last message ID, <c>ProofWrappers</c>.</summary>
    public const int MessageIdSpaceSize = Eth73MessageCode.ProofWrappers + 1;

    /// <summary>The <c>eth</c> soft response limit (2 MiB).</summary>
    public const int SoftResponseLimit = 2 * 1024 * 1024;

    /// <summary>EIP-8288 <c>MAX_WRAPPER_BYTES</c>, at most <see cref="SoftResponseLimit"/>.</summary>
    public const int MaxWrapperBytes = Eip8288Constants.MaxWrapperBytes;

    /// <summary>RLP framing of a full ProofWrappers response beyond its wrappers: per entry a list prefix and the 32-byte hash,
    /// and the request ID with the two outer list prefixes.</summary>
    public const int ResponseFramingBytes = MaxWrappersPerRequest * (4 + 33) + 19;

    /// <summary>The largest valid ProofWrappers message data, checked before decompression.</summary>
    /// <remarks>EIP-8437: a responder stops adding wrappers once the response reaches the soft limit, so the last wrapper may
    /// take it above the limit by at most one <see cref="MaxWrapperBytes"/> wrapper; the rest is RLP framing. Exceeding the
    /// soft limit alone is not a violation.</remarks>
    public const int LocalMaxResponseBytes = SoftResponseLimit + MaxWrapperBytes + ResponseFramingBytes;

    /// <summary>The largest valid NewProofWrapperHashes, 64 <c>[mode, length, hash]</c> entries at their widest, bounding
    /// message data before decompression (EIP-8437 check_vectors.py).</summary>
    public const int MaxNewProofWrapperHashesBytes = 2499;

    /// <summary>The largest valid GetProofWrappers, 32 hashes under the widest request ID, bounding message data before
    /// decompression (EIP-8437 check_vectors.py).</summary>
    public const int MaxGetProofWrappersBytes = 1072;

    /// <summary>Entries per NewProofWrapperHashes.</summary>
    public const int MaxAnnouncements = 64;

    /// <summary>Hashes per GetProofWrappers.</summary>
    public const int MaxWrappersPerRequest = 32;

    /// <summary>Nesting bound of the strict decoder; a wrapper nests three lists deep inside a two-list response.</summary>
    public const int MaxRlpDepth = 8;

    /// <summary>Whether an advertised uncompressed size exceeds this client's cap for an <c>eth/73</c> wrapper message,
    /// checked before decompression.</summary>
    public static bool ExceedsMessageLimit(int code, int uncompressedLength) => code switch
    {
        Eth73MessageCode.ProofWrappers => uncompressedLength > LocalMaxResponseBytes,
        Eth73MessageCode.NewProofWrapperHashes => uncompressedLength > MaxNewProofWrapperHashesBytes,
        Eth73MessageCode.GetProofWrappers => uncompressedLength > MaxGetProofWrappersBytes,
        _ => false
    };
}

/// <summary>Local transport policy: the request, serving and validation bounds EIP-8437 asks clients to keep.</summary>
/// <remarks>Refusing work under these limits is never a peer penalty; different values are not misbehavior.</remarks>
public static class WrapperTransportLimits
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How long a wanted wrapper may take from its first request to the end of its validation.</summary>
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(120);
    public const int MaxSourcesPerWrapper = 3;

    /// <summary>Outstanding GetProofWrappers requests per peer.</summary>
    public const int MaxRequestsPerPeer = 4;

    /// <summary>Outstanding GetProofWrappers requests to a peer whose wrappers repeatedly failed verification.</summary>
    public const int MaxRequestsPerFailingPeer = 1;

    /// <summary>The verification-failure score from which fetching from a peer is deprioritized and rate-limited.</summary>
    public const double DeprioritizedFailureScore = 3;

    /// <summary>The cap on a peer's verification-failure score, so it recovers within a few half-lives.</summary>
    public const double MaxFailureScore = 16;

    /// <summary>The half-life of a peer's verification-failure score.</summary>
    public static readonly TimeSpan FailureHalfLife = TimeSpan.FromMinutes(5);

    /// <summary>ProofWrappers data served per peer per second, which is also the burst.</summary>
    public const long ServeBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>NewProofWrapperHashes messages per peer per second, in either direction.</summary>
    public const int AnnouncementsPerSecond = 4;

    /// <summary>Wanted announced wrappers kept while no request slot is free, across all peers.</summary>
    public const int MaxPendingAnnouncements = 1024;

    /// <summary>Wanted announced wrappers one peer's announcements may hold in the pending cache.</summary>
    public const int MaxPendingAnnouncementsPerPeer = MaxPendingAnnouncements / 4;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Bytes charged to one peer's outstanding responses and received wrappers awaiting validation.</summary>
    public const long MaxPeerBytes = 4L * Eth73Protocol.LocalMaxResponseBytes + 2L * Eth73Protocol.MaxWrapperBytes;

    /// <summary>Bytes charged across all peers.</summary>
    public const long MaxGlobalBytes = 64L * 1024 * 1024;

    public const int MaxValidationWorkers = MaxRequestsPerPeer;
    public const long MaxStoreBytes = 128L * 1024 * 1024;
    public const int MaxStoreObjects = 256;

    /// <summary>Bytes of encodings cached from one peer for resolving its hash references.</summary>
    public const long MaxCachedEncodingBytesPerPeer = 2L * Eth73Protocol.MaxWrapperBytes;

    /// <summary>Bytes of encodings cached across all peers.</summary>
    public const long MaxCachedEncodingBytes = 64L * 1024 * 1024;

    public const int MaxTombstones = 4096;
    public const int MaxKnownPerPeer = 4096;
    public static readonly TimeSpan SoftTombstone = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
}
