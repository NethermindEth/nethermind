// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>The move of one rollup from one state to the next, and the ether it gains or loses.</summary>
public sealed record StateUpdate(ulong RollupId, ValueHash256 CurrentState, ValueHash256 NewState, Int256.Int256 EtherDelta);

public sealed record ExpectedStateRoot(ulong RollupId, ValueHash256 StateRoot);

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
    StateUpdate[] StateUpdates,
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] Calls,
    ExpectedCall[] ExpectedCalls,
    ValueHash256 RollingHash,
    ulong DestinationRollupId,
    bool Success,
    byte[] ReturnData);

public sealed record StaticExecutionEntry(
    ExpectedStateRoot[] ExpectedStateRoots,
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] Calls,
    ValueHash256 RollingHash,
    ulong DestinationRollupId,
    bool Success,
    byte[] ReturnData);

public sealed record RollupProofSystems(ulong RollupId, ulong[] ProofSystemIndexes);

/// <summary>The argument of <c>EEZ.postAndVerifyBatch</c>.</summary>
public sealed record PostBatch(
    ExpectedStateRoot[] ExpectedStateRoots,
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

public sealed record L2StaticExecutionEntry(
    ValueHash256 ProxyEntryHash,
    CrossChainCall[] IncomingCalls,
    ValueHash256 RollingHash,
    bool Success,
    byte[] ReturnData);
