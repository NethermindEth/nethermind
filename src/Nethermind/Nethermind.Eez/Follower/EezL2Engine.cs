// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Eez.Follower;

/// <summary>The engine calls the follower makes on its own node: insert a derived block, then move the heads.</summary>
public interface IEezL2Engine
{
    /// <exception cref="EezFollowerException">The node does not accept the block as valid.</exception>
    Task Insert(Block block);

    /// <exception cref="EezFollowerException">The node does not accept the forkchoice.</exception>
    Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized);

    /// <exception cref="EezFollowerException">Something other than the follower moved the node's forkchoice.</exception>
    void EnsureSoleDriver();
}

/// <summary>
/// Drives the node's own engine API in process, as an external consensus client would over RPC. The follower must be
/// the only driver: the engine endpoint stays open, as it does for the Optimism consensus layer, so a consensus
/// client connected to it could move the heads too. Every forkchoice the node applies is checked against the one the
/// follower sent, and one it did not send stops the follower.
/// </summary>
public sealed class EezL2Engine : IEezL2Engine, IDisposable
{
    private readonly IEngineRpcModule _engine;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private (Hash256 Head, Hash256 Safe, Hash256 Finalized)? _expected;
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
        if (result.Result.ResultType != ResultType.Success || result.Data.Status != PayloadStatus.Valid)
        {
            throw new EezFollowerException($"The node does not accept derived block {block.ToString(Block.Format.Short)}: " +
                $"{result.Data?.Status ?? result.Result.Error} {result.Data?.ValidationError}");
        }
    }

    public async Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized)
    {
        _expected = (head, safe, finalized);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await _engine.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(head, finalized, safe));
        if (result.Result.ResultType != ResultType.Success || result.Data.PayloadStatus.Status != PayloadStatus.Valid)
        {
            throw new EezFollowerException($"The node does not accept head {head}, safe {safe}, finalized {finalized}: " +
                $"{result.Data?.PayloadStatus.Status ?? result.Result.Error} {result.Data?.PayloadStatus.ValidationError}");
        }
    }

    public void EnsureSoleDriver()
    {
        if (_foreignForkchoice is { } foreign)
        {
            throw new EezFollowerException($"Another consensus client is driving this node's engine API ({foreign}); the EEZ follower must be its only driver.");
        }
    }

    private void OnForkChoiceUpdated(object? sender, IBlockTree.ForkChoiceUpdateEventArgs e)
    {
        if (_expected is not { } expected || _foreignForkchoice is not null)
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
}
