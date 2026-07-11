// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class StreamedSenderRecoveryTests
{
    private StreamedSenderRecovery _recovery = null!;

    [SetUp]
    public void SetUp()
    {
        EthereumEcdsa ecdsa = new(MainnetSpecProvider.Instance.ChainId);
        _recovery = new StreamedSenderRecovery(
            new RecoverSignatures(ecdsa, MainnetSpecProvider.Instance, LimboLogs.Instance),
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);
    }

    // The (join kind × recovery in flight) matrix. Joining an in-flight recovery must leave the
    // senders recovered; without one, the Ensure* joins are no-ops (the preprocessor owns those
    // blocks) while the preprocessor path itself recovers exactly as before streaming existed.
    // (in flight + preprocessor) is excluded: the skip leaves completion to the concurrent task,
    // so the observable outcome depends on timing.
    [TestCase(true, JoinKind.EnsureSendersRecovered, true, TestName = "BlockJoin_WithRecoveryInFlight_RecoversAllSenders")]
    [TestCase(false, JoinKind.EnsureSendersRecovered, false, TestName = "BlockJoin_WithoutRecoveryInFlight_IsNoOp")]
    [TestCase(true, JoinKind.EnsureSenderRecovered, true, TestName = "PerTxJoin_WithRecoveryInFlight_RecoversEachSender")]
    [TestCase(false, JoinKind.EnsureSenderRecovered, false, TestName = "PerTxJoin_WithoutRecoveryInFlight_IsNoOp")]
    [TestCase(false, JoinKind.RecoverData, true, TestName = "Preprocessor_WithoutRecoveryInFlight_RecoversAllSenders")]
    public void Join_AtGivenRecoveryState_LeavesExpectedSenders(bool recoveryInFlight, JoinKind joinKind, bool expectRecovered)
    {
        Block block = BuildBlockWithUnrecoveredSenders();

        if (recoveryInFlight)
        {
            _recovery.Begin(block);
        }

        Join(block, joinKind);

        foreach (Transaction tx in block.Transactions)
        {
            if (expectRecovered)
            {
                Assert.That(tx.SenderAddress, Is.Not.Null, "this join must not return before the senders are recovered");
            }
            else
            {
                Assert.That(tx.SenderAddress, Is.Null, "a block without recovery in flight must be left to the preprocessor");
            }
        }
    }

    [Test]
    public void Begin_WithEmptyBlock_TracksNothing()
    {
        Block block = Build.A.Block.TestObject;
        Assert.That(block.Transactions, Is.Empty, "precondition: the block under test carries no transactions");

        _recovery.Begin(block);
        _recovery.EnsureSendersRecovered(block, CancellationToken.None);
    }

    private void Join(Block block, JoinKind joinKind)
    {
        switch (joinKind)
        {
            case JoinKind.EnsureSendersRecovered:
                _recovery.EnsureSendersRecovered(block, CancellationToken.None);
                break;
            case JoinKind.EnsureSenderRecovered:
                foreach (Transaction tx in block.Transactions)
                {
                    _recovery.EnsureSenderRecovered(block, tx);
                }
                break;
            case JoinKind.RecoverData:
                _recovery.RecoverData(block);
                break;
        }
    }

    private static Block BuildBlockWithUnrecoveredSenders()
    {
        Transaction[] txs =
        [
            Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];
        foreach (Transaction tx in txs)
        {
            tx.SenderAddress = null;
        }

        return Build.A.Block.WithTransactions(txs).TestObject;
    }

    public enum JoinKind
    {
        EnsureSendersRecovered,
        EnsureSenderRecovered,
        RecoverData,
    }
}
