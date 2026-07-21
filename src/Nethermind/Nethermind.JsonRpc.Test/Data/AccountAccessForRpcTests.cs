// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

[Parallelizable(ParallelScope.All)]
[TestFixture]
public class AccountAccessForRpcTests
{
    [Test]
    public void Serializes_to_execution_apis_account_access_schema()
    {
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            storageChanges: [new ReadOnlySlotChanges(new UInt256(2), [new StorageChange(3, new UInt256(0x100))])],
            storageReads: [new UInt256(1)],
            balanceChanges: [new BalanceChange(1, new UInt256(0xa410))],
            nonceChanges: [new NonceChange(2, 5)],
            codeChanges: [new CodeChange(4, [0xde, 0xad, 0xbe, 0xef])]);
        ReadOnlyBlockAccessList blockAccessList = new([accountChanges], itemCount: 5);

        string json = new EthereumJsonSerializer().Serialize(AccountAccessForRpc.FromBlockAccessList(blockAccessList));

        // Bare array; 32-byte zero-padded storage keys/values/reads; hex-quantity indices and
        // balance/nonce values; code changes carry only index and code.
        Assert.That(json, Is.EqualTo(
            "[{\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"," +
            "\"storageChanges\":[{\"key\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"," +
            "\"changes\":[{\"index\":\"0x3\",\"value\":\"0x0000000000000000000000000000000000000000000000000000000000000100\"}]}]," +
            "\"storageReads\":[\"0x0000000000000000000000000000000000000000000000000000000000000001\"]," +
            "\"balanceChanges\":[{\"index\":\"0x1\",\"value\":\"0xa410\"}]," +
            "\"nonceChanges\":[{\"index\":\"0x2\",\"value\":\"0x5\"}]," +
            "\"codeChanges\":[{\"index\":\"0x4\",\"code\":\"0xdeadbeef\"}]}]"));
    }
}
