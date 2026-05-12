// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Consensus.Rewards;
using Nethermind.Db;
using Nethermind.Int256;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nethermind.Xdc;

internal sealed class RewardsStore(IDb rewardsDb) : IRewardsStore
{
    private const byte EpochMarkerPrefix = 0x00;
    private const byte AccountRewardPrefix = 0x01;
    private static readonly byte[] OldestRetainedEpochKey = [0x02];
    private static readonly byte[] NewestRetainedEpochKey = [0x03];

    public void SaveEpochRewards(ulong epochBlockNumber, BlockReward[] rewards)
    {
        Dictionary<Address, UInt256> rewardsByAccount = new();
        foreach (BlockReward reward in rewards)
        {
            if (!rewardsByAccount.TryAdd(reward.Address, reward.Value))
            {
                rewardsByAccount[reward.Address] += reward.Value;
            }
        }

        using IWriteBatch batch = rewardsDb.StartWriteBatch();
        byte[] epochMarkerKey = BuildEpochMarkerKey(epochBlockNumber);
        batch[epochMarkerKey] = [1];

        foreach ((Address account, UInt256 reward) in rewardsByAccount)
        {
            byte[] rewardKey = BuildAccountRewardKey(account, epochBlockNumber);
            byte[] rewardValue = new byte[32];
            reward.ToBigEndian(rewardValue);
            batch[rewardKey] = rewardValue;
        }

        UpdateRetentionMetadata(batch, epochBlockNumber);
        PruneOldEpochs(batch, epochBlockNumber);
    }

    public bool HasEpochRewards(ulong epochBlockNumber) => rewardsDb.KeyExists(BuildEpochMarkerKey(epochBlockNumber));

    public bool TryGetAccountReward(Address account, ulong epochBlockNumber, out UInt256 reward)
    {
        byte[]? rewardBytes = rewardsDb.Get(BuildAccountRewardKey(account, epochBlockNumber));
        if (rewardBytes is null)
        {
            reward = UInt256.Zero;
            return false;
        }

        reward = new UInt256(rewardBytes, isBigEndian: true);
        return true;
    }

    public bool TryGetRetainedRange(out ulong oldestEpochBlockNumber, out ulong newestEpochBlockNumber)
    {
        oldestEpochBlockNumber = 0;
        newestEpochBlockNumber = 0;

        byte[]? oldestBytes = rewardsDb.Get(OldestRetainedEpochKey);
        byte[]? newestBytes = rewardsDb.Get(NewestRetainedEpochKey);
        if (oldestBytes is null || newestBytes is null)
        {
            return false;
        }

        oldestEpochBlockNumber = BinaryPrimitives.ReadUInt64BigEndian(oldestBytes);
        newestEpochBlockNumber = BinaryPrimitives.ReadUInt64BigEndian(newestBytes);
        return true;
    }

    private static byte[] BuildEpochMarkerKey(ulong epochBlockNumber)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = EpochMarkerPrefix;
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1), epochBlockNumber);
        return key;
    }

    private static byte[] BuildAccountRewardKey(Address account, ulong epochBlockNumber)
    {
        byte[] key = new byte[1 + Address.Size + sizeof(ulong)];
        key[0] = AccountRewardPrefix;
        account.Bytes.CopyTo(key.AsSpan(1, Address.Size));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(1 + Address.Size), epochBlockNumber);
        return key;
    }

    private static bool IsEpochMarkerKey(ReadOnlySpan<byte> key) => key.Length == 1 + sizeof(ulong) && key[0] == EpochMarkerPrefix;

    private static bool IsAccountRewardKey(ReadOnlySpan<byte> key) => key.Length == 1 + Address.Size + sizeof(ulong) && key[0] == AccountRewardPrefix;

    private static ulong ExtractEpochFromEpochMarkerKey(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt64BigEndian(key[1..]);

    private static ulong ExtractEpochFromAccountRewardKey(ReadOnlySpan<byte> key) => BinaryPrimitives.ReadUInt64BigEndian(key[(1 + Address.Size)..]);

    private void UpdateRetentionMetadata(IWriteBatch batch, ulong currentEpochBlockNumber)
    {
        byte[]? oldestBytes = rewardsDb.Get(OldestRetainedEpochKey);
        if (oldestBytes is null)
        {
            byte[] first = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(first, currentEpochBlockNumber);
            batch[OldestRetainedEpochKey] = first;
        }

        byte[] latest = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(latest, currentEpochBlockNumber);
        batch[NewestRetainedEpochKey] = latest;
    }

    private void PruneOldEpochs(IWriteBatch batch, ulong currentEpochBlockNumber)
    {
        if (XdcConstants.RewardHistoryEpochRetention == 0 || currentEpochBlockNumber < XdcConstants.RewardHistoryEpochRetention)
        {
            return;
        }

        ulong cutoffEpochBlockNumber = currentEpochBlockNumber - XdcConstants.RewardHistoryEpochRetention;
        ulong? newOldest = null;

        foreach (KeyValuePair<byte[], byte[]?> entry in rewardsDb.GetAll())
        {
            ReadOnlySpan<byte> key = entry.Key;
            if (IsEpochMarkerKey(key))
            {
                ulong epoch = ExtractEpochFromEpochMarkerKey(key);
                if (epoch <= cutoffEpochBlockNumber)
                {
                    batch.Remove(entry.Key);
                }
                else
                {
                    newOldest = newOldest is null ? epoch : Math.Min(newOldest.Value, epoch);
                }
            }
            else if (IsAccountRewardKey(key))
            {
                ulong epoch = ExtractEpochFromAccountRewardKey(key);
                if (epoch <= cutoffEpochBlockNumber)
                {
                    batch.Remove(entry.Key);
                }
            }
        }

        if (newOldest.HasValue)
        {
            byte[] oldest = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(oldest, newOldest.Value);
            batch[OldestRetainedEpochKey] = oldest;
        }
    }
}
