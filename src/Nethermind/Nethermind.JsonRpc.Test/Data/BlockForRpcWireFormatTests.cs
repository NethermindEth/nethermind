// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

/// <summary>
/// RPC clients parse block responses byte for byte, so <c>transactions</c> in every block shape must keep the wire format
/// captured from the release these fixtures came from.
/// </summary>
public class BlockForRpcWireFormatTests
{
    /// <summary>The blocks the wire-format fixtures cover, named after their fixture.</summary>
    public static IEnumerable<TestCaseData> Blocks()
    {
        foreach ((string name, Block block) in BlockShapes())
        {
            yield return new TestCaseData($"{name}-full", new BlockForRpc(block, includeFullTransactionData: true, MainnetSpecProvider.Instance)).SetArgDisplayNames($"{name}, full");
            yield return new TestCaseData($"{name}-hashes", new BlockForRpc(block, includeFullTransactionData: false, MainnetSpecProvider.Instance)).SetArgDisplayNames($"{name}, hashes");
        }

        Block cancun = BlockShapes().First().Block;
        yield return new TestCaseData("cancun-skip", new BlockForRpc(cancun, includeFullTransactionData: true, MainnetSpecProvider.Instance, skipTxs: true)).SetArgDisplayNames("cancun, no transactions field");
        yield return new TestCaseData("simulate-full", new SimulateBlockResult<SimulateCallResult>(cancun, includeFullTransactionData: true, MainnetSpecProvider.Instance) { Calls = [] })
            .SetArgDisplayNames("eth_simulateV1, full");
        yield return new TestCaseData("simulate-hashes", new SimulateBlockResult<SimulateCallResult>(cancun, includeFullTransactionData: false, MainnetSpecProvider.Instance) { Calls = [] })
            .SetArgDisplayNames("eth_simulateV1, hashes");

        AuRaBlockHeader aura = AuRaBlockHeader.UpgradeFrom(Build.A.BlockHeader.WithNumber(5).TestObject);
        aura.AuRaStep = 42;
        aura.AuRaSignature = [7, 8, 9];
        Block auraBlock = new(aura, cancun.Body);
        yield return new TestCaseData("aura-full", new AuRaBlockForRpc(auraBlock, includeFullTransactionData: true, MainnetSpecProvider.Instance)).SetArgDisplayNames("AuRa, full");
        yield return new TestCaseData("aura-hashes", new AuRaBlockForRpc(auraBlock, includeFullTransactionData: false, MainnetSpecProvider.Instance)).SetArgDisplayNames("AuRa, hashes");
    }

    [TestCaseSource(nameof(Blocks))]
    public void Block_response_keeps_its_wire_format(string fixture, BlockForRpc block) =>
        JsonFixture.AssertMatches(typeof(BlockForRpcWireFormatTests).Assembly, fixture, Serialize(block));

    /// <summary>Transactions of every RPC type, so each dispatch target appears in the fixtures.</summary>
    public static Transaction[] AllTypes()
    {
        AccessList accessList = new AccessList.Builder().AddAddress(TestItem.AddressC).AddStorage(UInt256.One).Build();
        return
        [
            Build.A.Transaction.WithType(TxType.Legacy).WithNonce(1).WithGasPrice(7).WithTo(TestItem.AddressB).WithValue(5).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.Legacy).WithTo(null).WithCode([0x60, 0x00]).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.AccessList).WithChainId(BlockchainIds.Mainnet).WithAccessList(accessList).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(BlockchainIds.Mainnet).WithMaxFeePerGas(30).WithMaxPriorityFeePerGas(2).WithData([9]).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.Blob).WithChainId(BlockchainIds.Mainnet).WithShardBlobTxTypeAndFields(2, isMempoolTx: false).WithMaxFeePerGas(30).SignedAndResolved().TestObject,
            Build.A.Transaction.WithType(TxType.SetCode).WithChainId(BlockchainIds.Mainnet).WithAuthorizationCodeIfAuthorizationListTx().WithMaxFeePerGas(30).SignedAndResolved().TestObject,
        ];
    }

    /// <summary>Serializes <paramref name="block"/> with the RPC response options.</summary>
    public static string Serialize(BlockForRpc block) =>
        Encoding.UTF8.GetString(TypeInfoJsonSerializer.SerializeToUtf8Bytes(block, block.GetType(), EthereumJsonSerializer.JsonOptions));

    private static IEnumerable<(string Name, Block Block)> BlockShapes()
    {
        yield return ("cancun", Build.A.Block.WithNumber(25_000_000).WithTimestamp(1_700_000_000).WithBaseFeePerGas(7).WithTransactions(AllTypes())
            .WithWithdrawals(Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(2).WithRecipient(TestItem.AddressB).WithAmount(3).TestObject)
            .WithBlobGasUsed(131072).WithExcessBlobGas(0).WithParentBeaconBlockRoot(TestItem.KeccakE).TestObject);
        yield return ("pre-merge", Build.A.Block.WithNumber(1).WithDifficulty(17).WithTotalDifficulty(17L).WithUncles(Build.A.BlockHeader.TestObject).TestObject);
        yield return ("empty", Build.A.Block.WithNumber(2).WithTransactions([]).WithWithdrawals([]).TestObject);
    }
}
