// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 <c>eth/73</c> proof wrapper messages; the version and IDs are placeholders assigned with the activating fork.</summary>
public static class Eth73MessageCode
{
    public const int AnnounceProofWrappers = 0x16;
    public const int GetProofWrappers = 0x17;
    public const int ProofWrappers = 0x18;
    public const int GetWrapperEnvelopes = 0x19;
    public const int WrapperEnvelopes = 0x1a;

    /// <summary>Whether <paramref name="code"/> is one of the five wrapper messages.</summary>
    public static bool IsWrapperMessage(int code) => code is >= AnnounceProofWrappers and <= WrapperEnvelopes;
}

/// <summary>EIP-8437 wire ceilings. These are transport ceilings, not consensus limits.</summary>
public static class Eth73Protocol
{
    /// <summary>One past the last message ID, <c>WrapperEnvelopes</c>.</summary>
    public const int MessageIdSpaceSize = Eth73MessageCode.WrapperEnvelopes + 1;

    /// <summary>The <c>eth</c> soft response limit (2 MiB).</summary>
    public const int SoftResponseLimit = 2 * 1024 * 1024;

    /// <summary>EIP-8288 <c>MAX_WRAPPER_BYTES</c>, at most <see cref="SoftResponseLimit"/>.</summary>
    public const int MaxWrapperBytes = Eip8288Constants.MaxWrapperBytes;

    /// <summary>This client's own cap on ProofWrappers and WrapperEnvelopes message data, checked before decompression.</summary>
    /// <remarks>EIP-8437 sets no hard response ceiling: a responder stops before the soft limit but always includes the first
    /// item, so a response holds one wrapper of up to <see cref="MaxWrapperBytes"/>, or one envelope of up to the soft limit, plus
    /// its RLP framing.</remarks>
    public const int LocalMaxResponseBytes = SoftResponseLimit + 1024;

    /// <summary>Message-data ceiling for the other wrapper messages.</summary>
    public const int MaxRequestBytes = 16 * 1024;

    public const int MaxAnnouncements = 64;
    public const int MaxWrappersPerRequest = 64;
    public const int MaxEnvelopesPerRequest = 1024;

    /// <summary>Outstanding GetProofWrappers and GetWrapperEnvelopes requests per direction and peer.</summary>
    public const int MaxRequestsPerPeer = 4;

    /// <summary>Nesting bound of the strict decoder; a wrapper nests three lists deep inside a two-list response.</summary>
    public const int MaxRlpDepth = 8;

    /// <summary>Whether an advertised uncompressed size exceeds the ceiling of an <c>eth/73</c> wrapper message, checked before
    /// decompression.</summary>
    public static bool ExceedsMessageLimit(int code, int uncompressedLength) => code switch
    {
        Eth73MessageCode.ProofWrappers or Eth73MessageCode.WrapperEnvelopes => uncompressedLength > LocalMaxResponseBytes,
        Eth73MessageCode.AnnounceProofWrappers or Eth73MessageCode.GetProofWrappers or Eth73MessageCode.GetWrapperEnvelopes
            => uncompressedLength > MaxRequestBytes,
        _ => false
    };
}

/// <summary>Local transport policy at the EIP-8437 recommended defaults, and the finite budgets the EIP requires.</summary>
/// <remarks>Refusing work under these limits is never a peer penalty; different values are not misbehavior.</remarks>
public static class WrapperTransportLimits
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecoveryDeadline = TimeSpan.FromSeconds(120);
    public const int MaxSourcesPerWrapper = 3;

    /// <summary>ProofWrappers and WrapperEnvelopes data served per peer per second, which is also the burst.</summary>
    public const long ServeBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>AnnounceProofWrappers messages per peer per second, in either direction.</summary>
    public const int AnnouncementsPerSecond = 4;

    /// <summary>Wanted descriptors kept while no request slot is free, across all peers.</summary>
    public const int MaxPendingDescriptors = 1024;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Bytes charged to one peer's outstanding responses, received wrappers and recovered envelopes.</summary>
    public const long MaxPeerBytes = 4L * Eth73Protocol.LocalMaxResponseBytes + 2L * Eth73Protocol.MaxWrapperBytes;

    /// <summary>Bytes charged across all peers.</summary>
    public const long MaxGlobalBytes = 64L * 1024 * 1024;

    public const int MaxPendingValidations = 16;
    public const int MaxValidationWorkers = Eth73Protocol.MaxRequestsPerPeer;
    public const long MaxStoreBytes = 128L * 1024 * 1024;
    public const int MaxStoreObjects = 256;
    public const int MaxTombstones = 4096;
    public const int MaxKnownPerPeer = 4096;
    public static readonly TimeSpan SoftTombstone = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
}
