// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.Messages;

[TestFixture, Parallelizable(ParallelScope.All)]
public class SnapMessageLimitsTests
{
    /// <summary>
    /// Each response limit must be large enough that a valid message fitting within
    /// <see cref="SnapMessageLimits.MaxResponseBytes"/> (3 MiB) can never exceed the limit.
    /// A too-low limit causes <see cref="RlpLimitException"/>, which disconnects and bans
    /// the peer for 15 minutes — silently killing SnapSync throughput.
    ///
    /// We compute the theoretical maximum item count from the minimum per-item RLP size.
    /// </summary>
    [Test]
    public void MaxResponseAccounts_accommodates_max_response_bytes()
    {
        // Minimum AccountRange entry: RLP sequence header (1) + 32-byte hash (33) + minimal account (empty nonce/balance/code/storage ~4 bytes + RLP overhead ~5) ≈ 43 bytes
        // Be conservative — use the smallest realistic entry (~40 bytes) to get the highest possible count.
        const int minAccountEntryBytes = 40;
        long maxTheoreticalAccounts = SnapMessageLimits.MaxResponseBytes / minAccountEntryBytes;

        SnapMessageLimits.MaxResponseAccounts.Should().BeGreaterThanOrEqualTo((int)maxTheoreticalAccounts,
            "MaxResponseAccounts must accommodate the maximum number of accounts that can fit in a {0}-byte response at {1} bytes/entry minimum",
            SnapMessageLimits.MaxResponseBytes, minAccountEntryBytes);
    }

    [Test]
    public void MaxResponseSlotsPerAccount_accommodates_max_response_bytes()
    {
        // Minimum StorageSlot entry: RLP sequence header (1) + 32-byte hash (33) + minimal 1-byte value RLP (2) = 36 bytes
        const int minSlotEntryBytes = 36;
        long maxTheoreticalSlots = SnapMessageLimits.MaxResponseBytes / minSlotEntryBytes;

        SnapMessageLimits.MaxResponseSlotsPerAccount.Should().BeGreaterThanOrEqualTo((int)maxTheoreticalSlots,
            "MaxResponseSlotsPerAccount must accommodate the maximum number of slots that can fit in a {0}-byte response at {1} bytes/entry minimum",
            SnapMessageLimits.MaxResponseBytes, minSlotEntryBytes);
    }

    [Test]
    public void Roundtrip_AccountRange_at_40k_accounts()
    {
        const int count = 40_000;
        AccountRangeMessageSerializer serializer = new();

        ArrayPoolList<PathWithAccount> accounts = new(count);
        for (int i = 0; i < count; i++)
        {
            accounts.Add(new PathWithAccount(TestItem.KeccakA, Build.An.Account.WithBalance(1).TestObject));
        }

        AccountRangeMessage msg = new()
        {
            RequestId = 1,
            PathsWithAccounts = accounts,
            Proofs = EmptyByteArrayList.Instance,
        };

        byte[] serialized = serializer.Serialize(msg);
        AccountRangeMessage deserialized = serializer.Deserialize(serialized);

        deserialized.PathsWithAccounts.Count.Should().Be(count);
    }

    [Test]
    public void Roundtrip_StorageRange_at_50k_slots()
    {
        const int slotCount = 50_000;
        StorageRangesMessageSerializer serializer = new();

        ArrayPoolList<PathWithStorageSlot> slots = new(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            slots.Add(new PathWithStorageSlot(TestItem.KeccakA, Rlp.Encode(new byte[] { 0x01 }).Bytes));
        }

        using StorageRangeMessage msg = new()
        {
            RequestId = 1,
            Slots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { slots },
            Proofs = new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty()),
        };

        byte[] serialized = serializer.Serialize(msg);
        using StorageRangeMessage deserialized = serializer.Deserialize(serialized);

        deserialized.Slots[0].Count.Should().Be(slotCount);
    }
}
