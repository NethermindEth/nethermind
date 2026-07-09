// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
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
    private const byte RpcEpochRewardsPrefix = 0x11;
    private const int AddressByteLength = 20;
    private const int UInt256ByteLength = 32;

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

        using IWriteBatch batch = _rewardsDb.StartWriteBatch();
        batch[BuildRpcEpochRewardsKey(epochBlockHash)] = JsonSerializer.SerializeToUtf8Bytes(rewards);
        batch[BuildEpochRewardsKey(epochBlockHash)] = SerializeEpochRewards(SumAccountRewards(rewards));
    }

    public void SaveEpochRewards(Hash256 epochBlockHash, Dictionary<string, Dictionary<string, Dictionary<string, string>>> rewards)
    {
        ArgumentNullException.ThrowIfNull(epochBlockHash);

        XdcEpochRewards epochRewards = new();
        if (rewards.TryGetValue(XdcConstants.RpcRewardSectionMasternode, out Dictionary<string, Dictionary<string, string>>? masternodeRewards))
            epochRewards.Rewards = masternodeRewards;
        if (rewards.TryGetValue(XdcConstants.RpcRewardSectionProtector, out Dictionary<string, Dictionary<string, string>>? protectorRewards))
            epochRewards.RewardsProtector = protectorRewards;
        if (rewards.TryGetValue(XdcConstants.RpcRewardSectionObserver, out Dictionary<string, Dictionary<string, string>>? observerRewards))
            epochRewards.RewardsObserver = observerRewards;

        SaveEpochRewards(epochBlockHash, epochRewards);
    }


    public void SaveEpochRewards(Hash256 epochBlockHash, BlockReward[] rewards)
    {
        ArgumentNullException.ThrowIfNull(epochBlockHash);

        Dictionary<Address, UInt256> rewardsByAccount = [];
        foreach (BlockReward reward in rewards)
        {
            if (!rewardsByAccount.TryAdd(reward.Address, reward.Value))
            {
                rewardsByAccount[reward.Address] += reward.Value;
            }
        }

        _rewardsDb[BuildEpochRewardsKey(epochBlockHash)] = SerializeEpochRewards(rewardsByAccount);
    }

    public bool HasEpochRewards(Hash256 epochBlockHash) =>
        _rewardsDb.KeyExists(BuildEpochRewardsKey(epochBlockHash))
        || _rewardsDb.KeyExists(BuildRpcEpochRewardsKey(epochBlockHash));

    public bool TryGetEpochRewards(Hash256 epochBlockHash, out XdcEpochRewards? rewards)
    {
        byte[]? bytes = _rewardsDb.Get(BuildRpcEpochRewardsKey(epochBlockHash));
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
        byte[]? epochRewardsBytes = _rewardsDb.Get(BuildEpochRewardsKey(epochBlockHash));
        if (epochRewardsBytes is null)
        {
            if (!TryGetEpochRewards(epochBlockHash, out XdcEpochRewards? breakdown)
                || breakdown is null)
            {
                reward = UInt256.Zero;
                return false;
            }

            reward = SumAccountReward(breakdown, account);
            return reward > UInt256.Zero;
        }

        Dictionary<Address, UInt256> rewards = DeserializeEpochRewards(epochRewardsBytes);
        return rewards.TryGetValue(account, out reward);
    }

    public void Dispose() => _blockTree.BlockAddedToMain -= OnBlockAddedToMain;

    private static byte[] BuildEpochRewardsKey(Hash256 epochBlockHash)
    {
        byte[] key = new byte[1 + Hash256.Size];
        key[0] = EpochRewardsPrefix;
        epochBlockHash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] BuildRpcEpochRewardsKey(Hash256 epochBlockHash)
    {
        byte[] key = new byte[1 + Hash256.Size];
        key[0] = RpcEpochRewardsPrefix;
        epochBlockHash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }


    private static byte[] SerializeEpochRewards(Dictionary<Address, UInt256> rewardsByAccount)
    {
        int entryLength = AddressByteLength + UInt256ByteLength;
        byte[] bytes = new byte[sizeof(int) + rewardsByAccount.Count * entryLength];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteInt32BigEndian(span, rewardsByAccount.Count);

        int offset = sizeof(int);
        foreach ((Address account, UInt256 reward) in rewardsByAccount)
        {
            account.Bytes.CopyTo(span.Slice(offset, AddressByteLength));
            offset += AddressByteLength;

            reward.ToBigEndian(span.Slice(offset, UInt256ByteLength));
            offset += UInt256ByteLength;
        }

        return bytes;
    }

    private static Dictionary<Address, UInt256> DeserializeEpochRewards(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        int count = BinaryPrimitives.ReadInt32BigEndian(span);
        Dictionary<Address, UInt256> rewardsByAccount = new(count);

        int offset = sizeof(int);
        for (int i = 0; i < count; i++)
        {
            Address address = new(span.Slice(offset, AddressByteLength));
            offset += AddressByteLength;

            UInt256 reward = new(span.Slice(offset, UInt256ByteLength), isBigEndian: true);
            offset += UInt256ByteLength;

            rewardsByAccount[address] = reward;
        }

        return rewardsByAccount;
    }

    private static Dictionary<Address, UInt256> SumAccountRewards(XdcEpochRewards breakdown)
    {
        Dictionary<Address, UInt256> rewardsByAccount = [];
        SumAccountRewards(breakdown.Rewards, rewardsByAccount);
        SumAccountRewards(breakdown.RewardsProtector, rewardsByAccount);
        SumAccountRewards(breakdown.RewardsObserver, rewardsByAccount);

        return rewardsByAccount;
    }

    private static void SumAccountRewards(
        Dictionary<string, Dictionary<string, string>> section,
        Dictionary<Address, UInt256> rewardsByAccount)
    {
        foreach (Dictionary<string, string> holdersBySigner in section.Values)
        {
            foreach ((string accountKey, string valueStr) in holdersBySigner)
            {
                if (Address.TryParse(accountKey, out Address account)
                    && UInt256.TryParse(valueStr, out UInt256 value))
                {
                    if (!rewardsByAccount.TryAdd(account, value))
                    {
                        rewardsByAccount[account] += value;
                    }
                }
            }
        }
    }

    private static UInt256 SumAccountReward(
        XdcEpochRewards breakdown,
        Address account)
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
