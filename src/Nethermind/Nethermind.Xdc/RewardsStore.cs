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

namespace Nethermind.Xdc;

internal sealed class RewardsStore(
    [KeyFilter(XdcRocksDbConfigFactory.XdcRewardsDbName)] IDb rewardsDb,
    int rewardHistoryEpochRetention = XdcConstants.RewardHistoryEpochRetention) : IRewardsStore
{
    private readonly IDb _rewardsDb = rewardsDb;
    private readonly int _rewardHistoryEpochRetention = rewardHistoryEpochRetention;

    private const byte EpochRewardsPrefix = 0x10;
    private const byte SequenceToEpochPrefix = 0x11;
    private const byte EpochToSequencePrefix = 0x12;
    private const byte EpochNumberToHashPrefix = 0x13;
    private static readonly byte[] OldestSequenceKey = [0x01];
    private static readonly byte[] NewestSequenceKey = [0x02];
    private static readonly byte[] RetainedCountKey = [0x03];

    public void SaveEpochRewards(Hash256 epochBlockHash, ulong epochBlockNumber, Dictionary<string, Dictionary<string, Dictionary<string, string>>> rewards)
    {
        using IWriteBatch batch = _rewardsDb.StartWriteBatch();

        byte[]? existingHashBytes = _rewardsDb.Get(BuildEpochNumberToHashKey(epochBlockNumber));
        if (existingHashBytes is not null && !existingHashBytes.AsSpan().SequenceEqual(epochBlockHash.Bytes))
        {
            batch.Remove(BuildEpochRewardsKey(new Hash256(existingHashBytes)));
        }

        byte[] epochRewardsKey = BuildEpochRewardsKey(epochBlockHash);
        byte[] rewardBytes = JsonSerializer.SerializeToUtf8Bytes(rewards);
        batch[epochRewardsKey] = rewardBytes;
        batch[BuildEpochNumberToHashKey(epochBlockNumber)] = epochBlockHash.Bytes.ToArray();

        ulong oldestSequence = TryReadUInt64(OldestSequenceKey, out ulong oldestSequenceValue) ? oldestSequenceValue : 0;
        ulong newestSequence = TryReadUInt64(NewestSequenceKey, out ulong newestSequenceValue) ? newestSequenceValue : 0;
        int retainedCount = TryReadInt32(RetainedCountKey, out int retainedCountValue) ? retainedCountValue : 0;

        byte[] epochToSequenceKey = BuildEpochToSequenceKey(epochBlockNumber);
        byte[]? existingSequenceBytes = _rewardsDb.Get(epochToSequenceKey);
        if (existingSequenceBytes is null)
        {
            ulong nextSequence = retainedCount == 0 ? 0 : newestSequence + 1;

            batch[BuildSequenceToEpochKey(nextSequence)] = ToBigEndian(epochBlockNumber);
            batch[epochToSequenceKey] = ToBigEndian(nextSequence);
            newestSequence = nextSequence;
            if (retainedCount == 0)
            {
                oldestSequence = nextSequence;
            }
            retainedCount++;
        }

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

    public bool TryGetRetainedRange(out ulong oldestEpochBlockNumber, out ulong newestEpochBlockNumber)
    {
        oldestEpochBlockNumber = 0;
        newestEpochBlockNumber = 0;

        if (!TryReadUInt64(OldestSequenceKey, out ulong oldestSequence) ||
            !TryReadUInt64(NewestSequenceKey, out ulong newestSequence))
        {
            return false;
        }

        byte[]? oldestEpochBytes = _rewardsDb.Get(BuildSequenceToEpochKey(oldestSequence));
        byte[]? newestEpochBytes = _rewardsDb.Get(BuildSequenceToEpochKey(newestSequence));
        if (oldestEpochBytes is null || newestEpochBytes is null)
        {
            return false;
        }

        oldestEpochBlockNumber = BinaryPrimitives.ReadUInt64BigEndian(oldestEpochBytes);
        newestEpochBlockNumber = BinaryPrimitives.ReadUInt64BigEndian(newestEpochBytes);
        return true;
    }

    private void PruneOldEpochs(IWriteBatch batch, ref ulong oldestSequence, ref int retainedCount)
    {
        if (_rewardHistoryEpochRetention <= 0)
        {
            return;
        }

        while (retainedCount > _rewardHistoryEpochRetention)
        {
            byte[]? oldestEpochBytes = _rewardsDb.Get(BuildSequenceToEpochKey(oldestSequence));
            if (oldestEpochBytes is null)
            {
                return;
            }
            ulong oldestEpoch = BinaryPrimitives.ReadUInt64BigEndian(oldestEpochBytes);

            byte[]? hashBytes = _rewardsDb.Get(BuildEpochNumberToHashKey(oldestEpoch));
            if (hashBytes is not null)
            {
                batch.Remove(BuildEpochRewardsKey(new Hash256(hashBytes)));
                batch.Remove(BuildEpochNumberToHashKey(oldestEpoch));
            }

            batch.Remove(BuildEpochToSequenceKey(oldestEpoch));
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

        foreach ((string sectionName, Dictionary<string, Dictionary<string, string>> section) in breakdown)
        {
            if (!IsRewardSection(sectionName))
                continue;

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

    private static bool IsRewardSection(string sectionName) => sectionName is
        XdcConstants.RpcRewardSectionMasternode or
        XdcConstants.RpcRewardSectionProtector or
        XdcConstants.RpcRewardSectionObserver;

    private static byte[] BuildEpochRewardsKey(Hash256 epochBlockHash)
    {
        byte[] key = new byte[1 + Hash256.Size];
        key[0] = EpochRewardsPrefix;
        epochBlockHash.Bytes.CopyTo(key.AsSpan(1));
        return key;
    }

    private static byte[] BuildEpochNumberToHashKey(ulong epochBlockNumber)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = EpochNumberToHashPrefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), epochBlockNumber);
        return key;
    }

    private static byte[] BuildSequenceToEpochKey(ulong sequence)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = SequenceToEpochPrefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), sequence);
        return key;
    }

    private static byte[] BuildEpochToSequenceKey(ulong epochBlockNumber)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = EpochToSequencePrefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), epochBlockNumber);
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
