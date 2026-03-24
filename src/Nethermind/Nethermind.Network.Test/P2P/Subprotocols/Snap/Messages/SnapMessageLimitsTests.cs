// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Collections;
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
    /// Response limits must be large enough that a valid message within
    /// <see cref="SnapMessageLimits.MaxResponseBytes"/> (3 MiB) never exceeds the limit.
    /// A too-low limit causes <see cref="RlpLimitException"/>, which disconnects and bans
    /// the peer for 15 minutes — silently killing SnapSync throughput.
    /// </summary>
    [TestCase(nameof(SnapMessageLimits.MaxResponseAccounts), SnapMessageLimits.MaxResponseAccounts, 40, TestName = "MaxResponseAccounts accommodates 3 MiB at ~40 bytes/account")]
    [TestCase(nameof(SnapMessageLimits.MaxResponseSlotsPerAccount), SnapMessageLimits.MaxResponseSlotsPerAccount, 36, TestName = "MaxResponseSlotsPerAccount accommodates 3 MiB at ~36 bytes/slot")]
    public void Response_limit_accommodates_max_response_bytes(string limitName, int limit, int minEntryBytes)
    {
        int maxTheoreticalItems = (int)(SnapMessageLimits.MaxResponseBytes / minEntryBytes);

        limit.Should().BeGreaterThanOrEqualTo(maxTheoreticalItems,
            "{0} must accommodate the maximum item count that fits in a {1}-byte response at {2} bytes/entry",
            limitName, SnapMessageLimits.MaxResponseBytes, minEntryBytes);
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

        using AccountRangeMessage msg = new()
        {
            RequestId = 1,
            PathsWithAccounts = accounts,
            Proofs = EmptyByteArrayList.Instance,
        };

        byte[] serialized = serializer.Serialize(msg);
        using AccountRangeMessage deserialized = serializer.Deserialize(serialized);

        deserialized.PathsWithAccounts.Count.Should().Be(count);
    }

    [Test]
    public void Roundtrip_StorageRange_at_50k_slots()
    {
        const int slotCount = 50_000;
        StorageRangesMessageSerializer serializer = new();

        byte[] encodedSlot = Rlp.Encode(new byte[] { 0x01 }).Bytes;
        ArrayPoolList<PathWithStorageSlot> slots = new(slotCount);
        for (int i = 0; i < slotCount; i++)
        {
            slots.Add(new PathWithStorageSlot(TestItem.KeccakA, encodedSlot));
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
