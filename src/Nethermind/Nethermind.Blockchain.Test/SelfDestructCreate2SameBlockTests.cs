// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Nethermind.Core.Container;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Regression tests for the metamorphic-contract bug observed on Sepolia block 4913057
/// (Shanghai-era, archive node, prewarming enabled).
///
/// In that block, transaction 45 (sender 0x0c27…) called SELFDESTRUCT on contract
/// X (= 0x140da4…9372). Transaction 57 (sender 0xf93bab…) sent 0.001 ETH to X,
/// creating an empty account at the destroyed address. Transaction 58 (same sender
/// 0xf93bab…) called the metamorphic factory at 0x030a571…d25 to redeploy at X
/// via CREATE2 → transient → CREATE → X.
///
/// On Nethermind 1.37.1 the redeploy reverts: CREATE inside the transient sees X
/// as still having code and returns address(0), so the factory's
/// `require(deployed != 0)` fails. The same tx executed via debug_traceCall against
/// the parent state (i.e. without applying the SELFDESTRUCT) reproduces the
/// identical wrong gas usage (0x17a758) and revert message, which means the
/// SELFDESTRUCT effect from tx 45 is not visible to tx 58 during real block
/// processing.
///
/// These tests reproduce the scenario at the BlockProcessor level (block production
/// + validation, prewarming enabled by default) on TestBlockchain.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class SelfDestructCreate2SameBlockTests
{
    /// <summary>
    /// Two-tx variant: SELFDESTRUCT then CREATE2 redeploy, different senders.
    /// </summary>
    [Test]
    public async Task Redeploy_via_CREATE2_succeeds_when_destroy_and_redeploy_have_different_senders()
    {
        await RunRedeployScenario(includeIntermediateValueTransfer: false);
    }

    /// <summary>
    /// Three-tx variant matching the actual Sepolia 4913057 layout: SELFDESTRUCT,
    /// then a value-transfer to the destroyed address (creates an empty account),
    /// then CREATE2-based redeploy.
    /// </summary>
    [Test]
    public async Task Redeploy_via_CREATE2_succeeds_after_value_transfer_recreates_account()
    {
        await RunRedeployScenario(includeIntermediateValueTransfer: true);
    }

    private static async Task RunRedeployScenario(bool includeIntermediateValueTransfer)
    {
        // Use Shanghai (pre-Cancun) so SELFDESTRUCT fully wipes the account
        // — matches the failing real-world block (Sepolia 4913057, Dec 2023).
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Shanghai.Instance);

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton(specProvider);
        });

        // Code that, when called by anyone, immediately self-destructs to msg.sender.
        // 0x33 = CALLER, 0xff = SELFDESTRUCT.
        byte[] selfDestructCode = [0x33, 0xff];
        byte[] selfDestructInit = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        // Factory bytecode: when called, runs CREATE2(value=msg.value, offset=0, len, salt=0)
        // over a copy of `selfDestructInit` placed in memory. ForCreate2Of pushes salt=0.
        byte[] factoryRuntime = Prepare.EvmCode.ForCreate2Of(selfDestructInit).Done;
        byte[] factoryInit = Prepare.EvmCode.ForInitOf(factoryRuntime).Done;

        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        long gasLimit = 1_000_000;

        // ---- Block 1: A deploys factory ------------------------------------------------
        Transaction deployFactoryTx = Build.A.Transaction
            .WithCode(factoryInit)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .WithGasPrice(20)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA, isEip155Enabled: true).TestObject;

        Address factoryAddr = ContractAddress.From(TestItem.AddressA, 0);
        Address selfDestructAddr = ContractAddress.From(factoryAddr, new byte[32], selfDestructInit);

        await chain.AddBlock(deployFactoryTx);

        // ---- Block 2: A calls factory → first metamorphic deploy at selfDestructAddr ----
        Transaction firstDeployTx = Build.A.Transaction
            .To(factoryAddr)
            .WithGasLimit(gasLimit)
            .WithNonce(1)
            .WithGasPrice(20)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA, isEip155Enabled: true).TestObject;

        await chain.AddBlock(firstDeployTx);

        // Sanity: contract exists at selfDestructAddr with the expected code.
        chain.StateReader.TryGetAccount(chain.BlockTree.Head!.Header, selfDestructAddr, out var preAcct)
            .Should().BeTrue("setup must place the metamorphic contract at the expected address");
        chain.StateReader.GetCode(preAcct.CodeHash).Should().Equal(selfDestructCode,
            "setup must have deployed the 0x33ff self-destruct stub");

        // ---- Test block: destroy + (optional value-transfer) + redeploy --------------
        // tx0 (A → X): triggers SELFDESTRUCT.
        // tx1 (C → X) optional: sends 1 wei to recreate an empty account at X (mirrors
        //                       user's tx 0x39 which transferred 0.001 ETH to the
        //                       destroyed contract before redeployment).
        // txLast (B → factory):  CREATE2-redeploy at X. Different sender from destroy.
        Transaction destroyTx = Build.A.Transaction
            .To(selfDestructAddr)
            .WithGasLimit(gasLimit)
            .WithNonce(2)
            .WithGasPrice(100)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA, isEip155Enabled: true).TestObject;

        Transaction redeployTx = Build.A.Transaction
            .To(factoryAddr)
            .WithGasLimit(gasLimit)
            .WithNonce(0)
            .WithGasPrice(50)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyB, isEip155Enabled: true).TestObject;

        Block testBlock;
        TxReceipt[] receipts;

        if (includeIntermediateValueTransfer)
        {
            // Same sender (B) for the value-transfer AND the redeploy, matching the
            // real Sepolia tx pattern where 0xf93bab… did both tx 0x39 and tx 0x3a.
            // This places both into the same prewarmer sender-group so the cache
            // populates from parent state with selfDestructAddr still having code,
            // and the redeploy's prewarming pre-execution sees the OLD account.
            Transaction valueTx = Build.A.Transaction
                .To(selfDestructAddr)
                .WithGasLimit(GasCostOf.Transaction)
                .WithValue(1)
                .WithNonce(0)
                .WithGasPrice(75)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyB, isEip155Enabled: true).TestObject;

            Transaction redeployTxAfterValueTransfer = Build.A.Transaction
                .To(factoryAddr)
                .WithGasLimit(gasLimit)
                .WithNonce(1)
                .WithGasPrice(50)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyB, isEip155Enabled: true).TestObject;

            testBlock = await chain.AddBlock(destroyTx, valueTx, redeployTxAfterValueTransfer);

            testBlock.Transactions.Length.Should().Be(3, "test block must contain all three txs");
            testBlock.Transactions[0].Hash!.Should().Be(destroyTx.Hash!, "destroy tx must be first");
            testBlock.Transactions[1].Hash!.Should().Be(valueTx.Hash!, "value-transfer tx must be second");
            testBlock.Transactions[2].Hash!.Should().Be(redeployTxAfterValueTransfer.Hash!, "redeploy tx must be last");

            receipts = chain.ReceiptStorage.Get(testBlock);
            receipts[0].StatusCode.Should().Be(StatusCode.Success, "destroy tx must succeed");
            receipts[1].StatusCode.Should().Be(StatusCode.Success, "value-transfer tx must succeed");
            receipts[2].StatusCode.Should().Be(StatusCode.Success,
                "redeploy tx must succeed — if it reverts, the SELFDESTRUCT effect from the destroy tx is not visible to the redeploy");
        }
        else
        {
            testBlock = await chain.AddBlock(destroyTx, redeployTx);

            testBlock.Transactions.Length.Should().Be(2, "test block must contain both txs");
            testBlock.Transactions[0].Hash!.Should().Be(destroyTx.Hash!, "destroy tx must be first");
            testBlock.Transactions[1].Hash!.Should().Be(redeployTx.Hash!, "redeploy tx must be second");

            receipts = chain.ReceiptStorage.Get(testBlock);
            receipts[0].StatusCode.Should().Be(StatusCode.Success, "destroy tx must succeed");
            receipts[1].StatusCode.Should().Be(StatusCode.Success,
                "redeploy tx must succeed — if it reverts, the SELFDESTRUCT effect from the destroy tx is not visible to the redeploy");
        }

        // Final state: contract redeployed at the same address with the same code.
        chain.StateReader.TryGetAccount(chain.BlockTree.Head!.Header, selfDestructAddr, out var postAcct)
            .Should().BeTrue("contract must exist at the metamorphic address after redeploy");
        chain.StateReader.GetCode(postAcct.CodeHash).Should().Equal(selfDestructCode,
            "redeployed code must equal the metamorphic stub");
    }
}
