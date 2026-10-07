// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Eez.Follower;

/// <summary>The engine calls the follower makes on its own node: insert a block, then move the heads.</summary>
public interface IEezL2Engine
{
    /// <exception cref="EezFollowerException">The node finds the block invalid.</exception>
    /// <exception cref="EezEngineUnavailableException">The node has not validated the block yet; retry.</exception>
    Task Insert(Block block);

    /// <exception cref="EezFollowerException">The node finds the forkchoice invalid.</exception>
    /// <exception cref="EezEngineUnavailableException">The node has not applied the forkchoice yet; retry.</exception>
    Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized);

    /// <exception cref="EezFollowerException">Something other than the follower moved the node's forkchoice.</exception>
    void EnsureSoleDriver();
}

/// <summary>
/// Drives the node's own engine API in process, as an external consensus client would over RPC. The follower must be
/// the only driver: the engine endpoint stays open, so a consensus client connected to it could move the heads too.
/// Every forkchoice the node applies is checked against the one the follower sent, and one it did not send stops the
/// follower. Only an invalid block or forkchoice is final; a node that is still processing, or busy, is asked again.
/// </summary>
public sealed class EezL2Engine : IEezL2Engine, IDisposable
{
    private readonly IEngineRpcModule _engine;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private Forkchoice? _expected;
    private volatile string? _foreignForkchoice;

    public EezL2Engine(IEngineRpcModule engine, IBlockTree blockTree, ILogManager logManager)
    {
        _engine = engine;
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger<EezL2Engine>();
        _blockTree.OnForkChoiceUpdated += OnForkChoiceUpdated;
    }

    public async Task Insert(Block block)
    {
        ResultWrapper<PayloadStatusV1> result = await _engine.engine_newPayloadV4(ExecutionPayloadV3.Create(block), [], block.ParentBeaconBlockRoot,
            block.ExecutionRequests ?? []);
        string? status = result.Result.ResultType == ResultType.Success ? result.Data.Status : null;
        if (status != PayloadStatus.Valid)
        {
            throw Refusal(status, result.ErrorCode,
                $"The node does not accept block {block.ToString(Block.Format.Short)}: {status ?? result.Result.Error} {result.Data?.ValidationError}");
        }
    }

    public async Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized)
    {
        Volatile.Write(ref _expected, new Forkchoice(head, safe, finalized));
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await _engine.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(head, finalized, safe));
        string? status = result.Result.ResultType == ResultType.Success ? result.Data.PayloadStatus.Status : null;
        if (status != PayloadStatus.Valid)
        {
            throw Refusal(status, result.ErrorCode, $"The node does not accept head {head}, safe {safe}, finalized {finalized}: " +
                $"{status ?? result.Result.Error} {result.Data?.PayloadStatus.ValidationError}");
        }
    }

    public void EnsureSoleDriver()
    {
        if (_foreignForkchoice is { } foreign)
        {
            throw new EezFollowerException($"Another consensus client is driving this node's engine API ({foreign}); the EEZ follower must be its only driver.");
        }
    }

    /// <summary>
    /// An invalid block or forkchoice, or a call the node rejects as malformed, is final. Anything else, a block still
    /// processing, a forkchoice on a block not processed yet, or a timed out engine lock, clears on its own.
    /// </summary>
    private static Exception Refusal(string? status, int errorCode, string message) =>
        status == PayloadStatus.Invalid || errorCode is ErrorCodes.InvalidParams or MergeErrorCodes.InvalidForkchoiceState
            ? new EezFollowerException(message)
            : new EezEngineUnavailableException(message);

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs e)
    {
        if (Volatile.Read(ref _expected) is not { } expected || _foreignForkchoice is not null)
        {
            return;
        }

        Hash256? head = e.Head?.Hash;
        if (head == expected.Head && _blockTree.SafeHash == expected.Safe && _blockTree.FinalizedHash == expected.Finalized)
        {
            return;
        }

        _foreignForkchoice = $"head {head}, safe {_blockTree.SafeHash}, finalized {_blockTree.FinalizedHash}";
        if (_logger.IsError) _logger.Error($"A forkchoice the EEZ follower did not send was applied: {_foreignForkchoice}.");
    }

    public void Dispose() => _blockTree.OnForkChoiceUpdated -= OnForkChoiceUpdated;

    private sealed record Forkchoice(Hash256 Head, Hash256 Safe, Hash256 Finalized);
}

/// <summary>The node's engine has not settled a call yet: it is still processing, or busy. The follower asks again on its next poll.</summary>
public sealed class EezEngineUnavailableException(string message) : Exception(message);
