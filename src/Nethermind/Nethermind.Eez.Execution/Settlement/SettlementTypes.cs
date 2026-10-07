// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The move of one rollup from one root to the next, and the ether it gains or loses.</summary>
/// <remarks>The ABI tuple orders it <c>(rollupId, int192 etherDelta, currentRoot, newRoot)</c>.</remarks>
public sealed record RollupUpdate(ulong RollupId, ValueHash256 CurrentRoot, ValueHash256 NewRoot, Int256.Int256 EtherDelta);

public sealed record ExpectedRoot(ulong RollupId, ValueHash256 Root);

/// <summary>
/// A cross-chain call as execution tables carry it: <c>L2ToL1Call</c> on L1 and <c>CrossChainCall</c> on L2 share
/// this layout.
/// </summary>
public sealed record CrossChainCall(
    ushort RevertNextNCalls,
    bool IsStatic,
    ulong Gas,
    Address SourceAddress,
    ulong SourceRollupId,
    Address TargetAddress,
    UInt256 Value,
    byte[] Data);

/// <summary>
/// A reentrant call the entry expects and its precomputed result: <c>ExpectedL1ToL2Call</c> on L1 and
/// <c>ExpectedOutgoingCrossChainCall</c> on L2 share this layout.
/// </summary>
public sealed record ExpectedCall(
    ValueHash256 ExpectedHash,
    CrossChainCall[] Calls,
    ValueHash256 RevertedOrStaticRollingHash,
    bool Success,
    byte[] ReturnData);

public sealed record ExecutionEntry(
    RollupUpdate[] RollupUpdates,
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] Calls,
    ExpectedCall[] ExpectedCalls,
    ValueHash256 RollingHash,
    ulong DestinationRollupId,
    bool Success,
    byte[] ReturnData);

public sealed record StaticExecutionEntry(
    ExpectedRoot[] ExpectedRoots,
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] Calls,
    ValueHash256 RollingHash,
    ulong DestinationRollupId,
    bool Success,
    byte[] ReturnData);

public sealed record RollupProofSystems(ulong RollupId, ulong[] ProofSystemIndexes);

/// <summary>The argument of <c>EEZ.postAndVerifyBatch</c>.</summary>
public sealed record PostBatch(
    ExpectedRoot[] ExpectedRoots,
    ExecutionEntry[] Entries,
    StaticExecutionEntry[] StaticEntries,
    UInt256 ImmediateEntryCount,
    UInt256 ImmediateStaticEntryCount,
    Address[] ProofSystems,
    RollupProofSystems[] RollupIdsWithProofSystems,
    UInt256[] BlobIndices,
    byte[] CallData,
    byte[][] Proofs,
    ulong BlockNumber,
    bool BindMsgSenderInPublicInput);

public sealed record L2ExecutionEntry(
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] IncomingCalls,
    ExpectedCall[] ExpectedOutgoingCalls,
    ValueHash256 RollingHash,
    bool Success,
    byte[] ReturnData);

/// <param name="ExpectedEntryIndex">The index of the execution entry whose call this static read answers.</param>
public sealed record L2StaticExecutionEntry(
    UInt256 ExpectedEntryIndex,
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] IncomingCalls,
    ValueHash256 RollingHash,
    bool Success,
    byte[] ReturnData);
