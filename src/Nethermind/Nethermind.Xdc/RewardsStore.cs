// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal sealed class RewardsStore(
    [KeyFilter(XdcRocksDbConfigFactory.XdcRewardsDbName)] IDb rewardsDb,
    IBlockTree blockTree,
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    ILogManager logManager) : IRewardsStore, IStartable, IDisposable
{
    private readonly IDb _rewardsDb = rewardsDb;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly IEpochSwitchManager _epochSwitchManager = epochSwitchManager;
    private readonly ISpecProvider _specProvider = specProvider;
    private readonly ILogger _logger = logManager.GetClassLogger<RewardsStore>();

    private const byte EpochRewardsPrefix = 0x10;

    public void Start() => _blockTree.BlockAddedToMain += OnBlockAddedToMain;

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        if (e.Block.Header is not XdcBlockHeader xdcHeader)
            return;

        if (e.Block.Hash is null || !_blockTree.WasProcessed(e.Block.Number, e.Block.Hash))
            return;

        if (xdcHeader.Number == 0)
            return;

        if (!_epochSwitchManager.IsEpochSwitchAtBlock(xdcHeader))
            return;

        ulong round = xdcHeader.ExtraConsensusData!.BlockRound;
        IXdcReleaseSpec spec = _specProvider.GetXdcSpec(xdcHeader, round);
        if (xdcHeader.Number == spec.SwitchBlock + 1)
            return;

        if (xdcHeader.ProcessedRewards is null)
        {
            if (_logger.IsDebug) _logger.Debug($"No rewards for epoch block #{xdcHeader.Number} hash=${xdcHeader.Hash}");
            return;
        }

        Block block = e.Block;
        try
        {
            SaveEpochRewards(xdcHeader.Hash, xdcHeader.ProcessedRewards.EpochRewards);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to persist epoch rewards for block #{block.Number}.", ex);
        }
    }

    public void SaveEpochRewards(Hash256 epochBlockHash, XdcEpochRewards rewards)
    {
        ArgumentNullException.ThrowIfNull(epochBlockHash);
        ArgumentNullException.ThrowIfNull(rewards);

        _rewardsDb[BuildKey(epochBlockHash)] = JsonSerializer.SerializeToUtf8Bytes(rewards);
    }

    public bool HasEpochRewards(Hash256 epochBlockHash) =>
        _rewardsDb.KeyExists(BuildKey(epochBlockHash));

    public bool TryGetEpochRewards(Hash256 epochBlockHash, out XdcEpochRewards? rewards)
    {
        byte[]? bytes = _rewardsDb.Get(BuildKey(epochBlockHash));
        if (bytes is null)
        {
            rewards = null;
            return false;
        }

        rewards = JsonSerializer.Deserialize<XdcEpochRewards>(bytes);
        return rewards is not null;
    }

    public bool TryGetAccountReward(Address account, Hash256 epochBlockHash, out UInt256 reward)
    {
        if (!TryGetEpochRewards(epochBlockHash, out XdcEpochRewards? breakdown) || breakdown is null)
        {
            reward = UInt256.Zero;
            return false;
        }

        reward = SumAccountReward(breakdown, account);
        return reward > UInt256.Zero;
    }

    public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;

    private static byte[] BuildKey(Hash256 epochBlockHash)
    {
        byte[] key = new byte[1 + Hash256.Size];
        key[0] = EpochRewardsPrefix;
        epochBlockHash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    private static UInt256 SumAccountReward(XdcEpochRewards breakdown, Address account)
    {
        string accountKey = account.ToString();
        UInt256 total = UInt256.Zero;
        SumAccountReward(breakdown.Rewards, accountKey, ref total);
        SumAccountReward(breakdown.RewardsProtector, accountKey, ref total);
        SumAccountReward(breakdown.RewardsObserver, accountKey, ref total);
        return total;
    }

    private static void SumAccountReward(
        Dictionary<string, Dictionary<string, string>> section,
        string accountKey,
        ref UInt256 total)
    {
        foreach (Dictionary<string, string> holdersBySigner in section.Values)
        {
            if (holdersBySigner.TryGetValue(accountKey, out string? valueStr)
                && UInt256.TryParse(valueStr, out UInt256 value))
            {
                total += value;
            }
        }
    }
}
