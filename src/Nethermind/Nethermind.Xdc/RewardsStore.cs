// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;

namespace Nethermind.Xdc;

internal sealed class RewardsStore(
    [KeyFilter(XdcRocksDbConfigFactory.XdcRewardsDbName)] IDb rewardsDb,
    IBlockTree blockTree,
    int rewardHistoryEpochRetention = XdcConstants.RewardHistoryEpochRetention) : IRewardsStore
{
    private readonly IDb _rewardsDb = rewardsDb;
    private readonly IBlockTree _blockTree = blockTree;
    private readonly int _rewardHistoryEpochRetention = rewardHistoryEpochRetention;

    private const byte EpochRewardsPrefix = 0x10;
    private const byte SequenceToEpochPrefix = 0x11;
    private static readonly byte[] OldestSequenceKey = [0x01];
    private static readonly byte[] NewestSequenceKey = [0x02];
    private static readonly byte[] RetainedCountKey = [0x03];

    public void SaveEpochRewards(Hash256 epochBlockHash, Dictionary<string, Dictionary<string, Dictionary<string, string>>> rewards)
    {
        using IWriteBatch batch = _rewardsDb.StartWriteBatch();

        byte[] epochRewardsKey = BuildEpochRewardsKey(epochBlockHash);
        bool alreadyStored = _rewardsDb.KeyExists(epochRewardsKey);
        batch[epochRewardsKey] = JsonSerializer.SerializeToUtf8Bytes(rewards);

        if (alreadyStored)
        {
            return;
        }

        ulong oldestSequence = TryReadUInt64(OldestSequenceKey, out ulong oldestSequenceValue) ? oldestSequenceValue : 0;
        ulong newestSequence = TryReadUInt64(NewestSequenceKey, out ulong newestSequenceValue) ? newestSequenceValue : 0;
        int retainedCount = TryReadInt32(RetainedCountKey, out int retainedCountValue) ? retainedCountValue : 0;

        ulong nextSequence = retainedCount == 0 ? 0 : newestSequence + 1;

        batch[BuildSequenceToEpochKey(nextSequence)] = epochBlockHash.Bytes.ToArray();
        newestSequence = nextSequence;
        if (retainedCount == 0)
        {
            oldestSequence = nextSequence;
        }

        retainedCount++;

        PruneOldEpochs(batch, ref oldestSequence, ref retainedCount);

        if (retainedCount > 0)
        {
            batch[OldestSequenceKey] = ToBigEndian(oldestSequence);
            batch[NewestSequenceKey] = ToBigEndian(newestSequence);
        }

        batch[RetainedCountKey] = ToBigEndian(retainedCount);
    }

    public bool HasEpochRewards(Hash256 epochBlockHash) => _rewardsDb.KeyExists(BuildEpochRewardsKey(epochBlockHash));

    public bool TryGetEpochRewards(Hash256 epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? rewards)
    {
        byte[]? bytes = _rewardsDb.Get(BuildEpochRewardsKey(epochBlockHash));
        if (bytes is null)
        {
            rewards = null;
            return false;
        }

        rewards = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(bytes);
        return rewards is not null;
    }

    public bool TryGetAccountReward(Address account, Hash256 epochBlockHash, out UInt256 reward)
    {
        if (!TryGetEpochRewards(epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? breakdown)
            || breakdown is null)
        {
            reward = UInt256.Zero;
            return false;
        }

        reward = SumAccountReward(breakdown, account);
        return reward > UInt256.Zero;
    }

    private void PruneOldEpochs(IWriteBatch batch, ref ulong oldestSequence, ref int retainedCount)
    {
        if (_rewardHistoryEpochRetention <= 0)
        {
            return;
        }

        while (retainedCount > _rewardHistoryEpochRetention)
        {
            byte[]? oldestEpochHashBytes = _rewardsDb.Get(BuildSequenceToEpochKey(oldestSequence));
            if (oldestEpochHashBytes is null)
            {
                return;
            }

            batch.Remove(BuildEpochRewardsKey(new Hash256(oldestEpochHashBytes)));
            batch.Remove(BuildSequenceToEpochKey(oldestSequence));

            oldestSequence++;
            retainedCount--;
        }
    }

    private static UInt256 SumAccountReward(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> breakdown,
        Address account)
    {
        string accountKey = account.ToString();
        UInt256 total = UInt256.Zero;

        foreach (Dictionary<string, Dictionary<string, string>> section in breakdown.Values)
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

        return total;
    }

    private static byte[] BuildEpochRewardsKey(Hash256 epochBlockHash)
    {
        byte[] key = new byte[1 + Hash256.Size];
        key[0] = EpochRewardsPrefix;
        epochBlockHash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] BuildSequenceToEpochKey(ulong sequence)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = SequenceToEpochPrefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), sequence);
        return key;
    }

    private bool TryReadUInt64(byte[] key, out ulong value)
    {
        byte[]? bytes = _rewardsDb.Get(key);
        if (bytes is null)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        return true;
    }

    private bool TryReadInt32(byte[] key, out int value)
    {
        byte[]? bytes = _rewardsDb.Get(key);
        if (bytes is null)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32BigEndian(bytes);
        return true;
    }

    private static byte[] ToBigEndian(ulong value)
    {
        byte[] bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] ToBigEndian(int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
