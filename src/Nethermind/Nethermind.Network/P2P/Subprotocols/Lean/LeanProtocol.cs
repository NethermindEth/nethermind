// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Wire constants of EIP-8437, proof object transport over devp2p.</summary>
/// <remarks>These are transport and mempool policy ceilings, not consensus validity limits; local limits in
/// <see cref="LeanLimits"/> may be lower.</remarks>
public static class LeanProtocol
{
    public const string Code = "lean";
    public const byte Version = 1;
    public const int MessageCount = 6;
    public const int ChunkBytes = 64 * 1024;
    public const ulong MaxObjectBytes = 9 * 1024 * 1024;
    public const int MaxMessageBytes = 128 * 1024;
    public const int MaxRlpDepth = 8;
    public const int MaxProfiles = 16;
    public const int MaxAnnouncements = 64;
    public const int MaxChunksPerRequest = 32;
    public const int MaxRequestsPerPeer = 4;
    public const int MaxChunkCount = (int)(MaxObjectBytes / ChunkBytes);
    public const int MaxCreditBytes = 2 * 1024 * 1024;
    public const ulong StatusVersion = 1;
}

/// <summary>Relative <c>lean/1</c> message IDs, numbered consecutively; any other ID is a protocol violation.</summary>
public static class LeanMessageCode
{
    public const int Status = 0x00;
    public const int AnnounceObjects = 0x01;
    public const int GetChunks = 0x02;
    public const int Chunk = 0x03;
    public const int Complete = 0x04;
    public const int Cancel = 0x05;
}

/// <summary>Terminal statuses of a GetChunks request.</summary>
public enum LeanCompleteStatus : byte
{
    Served = 0,
    Unavailable = 1,
    Busy = 2,
    Unsupported = 3,
    Cancelled = 4,
    TooLarge = 5
}

/// <summary>Local limits below the EIP-8437 wire ceilings, and the local request and assembly lifetime policy.</summary>
/// <remarks>Refusing work under these limits is never a peer penalty. Lifetimes and the source-attempt budget use the
/// EIP-8437 recommended defaults; they are finite, never negotiated, and not renewed by retries or source changes.</remarks>
public static class LeanLimits
{
    /// <summary>Advertised receive ceiling: room for a maximum wrapper.</summary>
    public const ulong MaxObjectBytes = LeanProtocol.MaxObjectBytes;
    public static readonly TimeSpan RequestIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestAge = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan AssemblyIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AssemblyAge = TimeSpan.FromSeconds(300);
    public const int MaxAssemblies = 32;
    public const int MaxAssembliesPerPeer = 8;

    /// <summary>Announced objects kept pending per peer while the assembly limits are reached.</summary>
    public const int MaxDeferredPerPeer = LeanProtocol.MaxAnnouncements;

    /// <summary>How long a pending announcement stays eligible for retrieval.</summary>
    public static readonly TimeSpan DeferredLifetime = AssemblyIdle;
    public const long MaxIncompleteBytes = 72 * 1024 * 1024;
    public const long MaxPeerBytes = 18 * 1024 * 1024;
    public const long MaxCompletionBytes = MaxIncompleteBytes;
    public const int MaxPendingValidations = 8;
    public const long MaxStoreBytes = 128 * 1024 * 1024;
    public const int MaxStoreObjects = 256;
    public const int MaxTombstones = 4096;
    public const int MaxKnownPerPeer = 4096;
    public const int MaxSourcesPerAssembly = 3;
    public const int MaxStalledRequests = 3;
    public const int ChunkBookkeepingBytes = 64;
    public static readonly TimeSpan StallThrottle = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SoftTombstone = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan TransactionRecovery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
}
