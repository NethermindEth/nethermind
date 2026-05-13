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

internal sealed class RewardsStore(IDb rewardsDb, int rewardHistoryEpochRetention = XdcConstants.RewardHistoryEpochRetention) : IRewardsStore
{
    private readonly IDb _rewardsDb = rewardsDb;
    private readonly int _rewardHistoryEpochRetention = rewardHistoryEpochRetention;

    private const byte EpochRewardsPrefix = 0x10;
    private const byte SequenceToEpochPrefix = 0x11;
    private const byte EpochToSequencePrefix = 0x12;
    private static readonly byte[] OldestSequenceKey = [0x01];
    private static readonly byte[] NewestSequenceKey = [0x02];
    private static readonly byte[] RetainedCountKey = [0x03];
    private const int AddressByteLength = 20;
    private const int UInt256ByteLength = 32;

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

        using IWriteBatch batch = _rewardsDb.StartWriteBatch();
        byte[] epochRewardsKey = BuildEpochRewardsKey(epochBlockNumber);
        byte[] rewardBytes = SerializeEpochRewards(rewardsByAccount);
        batch[epochRewardsKey] = rewardBytes;

        byte[] epochToSequenceKey = BuildEpochToSequenceKey(epochBlockNumber);
        byte[]? existingSequenceBytes = _rewardsDb.Get(epochToSequenceKey);
        if (existingSequenceBytes is null)
        {
            ulong nextSequence = TryReadUInt64(NewestSequenceKey, out ulong newestSequence) ? newestSequence + 1 : 0;

            batch[BuildSequenceToEpochKey(nextSequence)] = ToBigEndian(epochBlockNumber);
            batch[epochToSequenceKey] = ToBigEndian(nextSequence);
            batch[NewestSequenceKey] = ToBigEndian(nextSequence);

            if (!TryReadUInt64(OldestSequenceKey, out _))
            {
                batch[OldestSequenceKey] = ToBigEndian(nextSequence);
            }

            int count = TryReadInt32(RetainedCountKey, out int currentCount) ? currentCount + 1 : 1;
            batch[RetainedCountKey] = ToBigEndian(count);

            PruneOldEpochs(batch);
        }
    }

    public bool HasEpochRewards(ulong epochBlockNumber) => _rewardsDb.KeyExists(BuildEpochRewardsKey(epochBlockNumber));

    public bool TryGetAccountReward(Address account, ulong epochBlockNumber, out UInt256 reward)
    {
        byte[]? epochRewardsBytes = _rewardsDb.Get(BuildEpochRewardsKey(epochBlockNumber));
        if (epochRewardsBytes is null)
        {
            reward = UInt256.Zero;
            return false;
        }

        Dictionary<Address, UInt256> rewards = DeserializeEpochRewards(epochRewardsBytes);
        return rewards.TryGetValue(account, out reward);
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

    private void PruneOldEpochs(IWriteBatch batch)
    {
        if (_rewardHistoryEpochRetention <= 0)
        {
            return;
        }

        while (TryReadInt32(RetainedCountKey, out int retainedCount) &&
               retainedCount > _rewardHistoryEpochRetention)
        {
            if (!TryReadUInt64(OldestSequenceKey, out ulong oldestSequence))
            {
                return;
            }

            byte[]? oldestEpochBytes = _rewardsDb.Get(BuildSequenceToEpochKey(oldestSequence));
            if (oldestEpochBytes is null)
            {
                return;
            }
            ulong oldestEpoch = BinaryPrimitives.ReadUInt64BigEndian(oldestEpochBytes);

            batch.Remove(BuildEpochRewardsKey(oldestEpoch));
            batch.Remove(BuildEpochToSequenceKey(oldestEpoch));
            batch.Remove(BuildSequenceToEpochKey(oldestSequence));

            ulong nextOldestSequence = oldestSequence + 1;
            batch[OldestSequenceKey] = ToBigEndian(nextOldestSequence);
            batch[RetainedCountKey] = ToBigEndian(retainedCount - 1);
        }
    }

    private static byte[] BuildEpochRewardsKey(ulong epochBlockNumber)
    {
        byte[] key = new byte[1 + sizeof(ulong)];
        key[0] = EpochRewardsPrefix;
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
            Address address = new(span.Slice(offset, AddressByteLength).ToArray());
            offset += AddressByteLength;

            UInt256 reward = new(span.Slice(offset, UInt256ByteLength), isBigEndian: true);
            offset += UInt256ByteLength;

            rewardsByAccount[address] = reward;
        }

        return rewardsByAccount;
    }
}
