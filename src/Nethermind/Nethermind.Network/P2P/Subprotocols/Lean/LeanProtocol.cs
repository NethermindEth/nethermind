// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Wire constants of EIP-8437, proof object transport over devp2p.</summary>
/// <remarks>These are transport and mempool policy ceilings, not consensus validity limits; local limits in
/// <see cref="LeanLimits"/> may be lower.</remarks>
public static class LeanProtocol
{
    public const string Code = "lean";
    public const byte Version = 1;
    public const int MessageCount = 10;
    public const int ChunkBytes = 64 * 1024;
    public const ulong MaxObjectBytes = 64 * 1024 * 1024;
    public const int MaxMessageBytes = 128 * 1024;
    public const int MaxRlpDepth = 8;
    public const int MaxProfiles = 16;
    public const int MaxAnnouncements = 64;
    public const int MaxLookups = 16;
    public const int MaxChunksPerRequest = 32;
    public const int MaxRequestsPerPeer = 4;
    public const int MaxTxsPerObject = 4096;
    public const int MaxDirectSigsPerWrapper = Eip8288Constants.MaxLeanSigDepsPerWrapper;
    public const int MaxDirectStarksPerWrapper = Eip8288Constants.MaxLeanStarkDepsPerWrapper;
    public const int MaxDepsPerAggregate = Eip8288Constants.MaxDepsPerAggregate;
    public const int MaxLeanStarkDepsPerAggregate = Eip8288Constants.MaxLeanStarkDepsPerAggregate;
    public const int MaxTxsPerRequest = 16;
    public const int MaxTxResponseBytes = 64 * 1024;
    public const int MaxMetadataResponseBytes = 64 * 1024;
    public const int MaxHeaderSkeletonBytes = 16 * 1024;
    public const int MaxHeaderFields = 64;
    public const int MaxDescriptorBytes = 512;
    public const int MaxChunkCount = (int)(MaxObjectBytes / ChunkBytes);
    public const int MaxCreditBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan MaxRequestIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan MaxAssemblyIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxAssemblyAge = TimeSpan.FromSeconds(300);
    public const ulong StatusVersion = 1;

    public const byte KindWrapper = 1;
    public const byte KindBlockProof = 2;
    public const byte KindInclusionList = 3;
    public const byte LookupPrimary = 0;
    public const byte LookupTransaction = 1;
}

/// <summary>Relative <c>lean/1</c> message IDs.</summary>
public static class LeanMessageCode
{
    public const int Status = 0x00;
    public const int AnnounceObjects = 0x01;
    public const int GetObjects = 0x02;
    public const int Objects = 0x03;
    public const int GetChunks = 0x04;
    public const int Chunk = 0x05;
    public const int Complete = 0x06;
    public const int Cancel = 0x07;
    public const int GetTransactions = 0x08;
    public const int Transactions = 0x09;
}

/// <summary>Result statuses of Objects and Transactions.</summary>
public enum LeanResultStatus : byte
{
    Ok = 0,
    Unavailable = 1,
    Busy = 2,
    Unsupported = 3,
    TooLarge = 4
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

/// <summary>Local limits below the EIP-8437 wire ceilings.</summary>
/// <remarks>Refusing work under these limits is never a peer penalty.</remarks>
public static class LeanLimits
{
    /// <summary>Advertised receive ceiling: room for a maximum wrapper and a maximum block proof.</summary>
    public const ulong MaxObjectBytes = 16 * 1024 * 1024;
    public const int MaxAssemblies = 32;
    public const int MaxAssembliesPerPeer = 8;
    public const long MaxIncompleteBytes = 64 * 1024 * 1024;
    public const long MaxPeerBytes = 24 * 1024 * 1024;
    public const long MaxCompletionBytes = 48 * 1024 * 1024;
    public const int MaxPendingValidations = 8;
    public const long MaxStoreBytes = 128 * 1024 * 1024;
    public const int MaxStoreObjects = 256;
    public const int MaxTombstones = 4096;
    public const int MaxKnownPerPeer = 4096;
    public const int MaxSourcesPerAssembly = 8;
    public const int MaxAvailabilityHints = 1024;
    public const int MaxStalledRequests = 3;
    public const int ChunkBookkeepingBytes = 64;
    public static readonly TimeSpan StallThrottle = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SoftTombstone = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan TransactionRecovery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
}
