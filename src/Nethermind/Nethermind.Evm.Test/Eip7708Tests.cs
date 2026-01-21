// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class Eip7708Tests
{
    [TestCase(true, 1_000_000ul, 1, TestName = "EIP-7708 enabled, transfer value > 0" )]
    [TestCase(true, 1ul, 1, TestName = "EIP-7708 enabled, transfer value = 1" )]
    [TestCase(true, 0ul, 0, TestName = "EIP-7708 enabled, transfer value = 0" )]
    [TestCase(false, 1_000_000ul, 0, TestName = "EIP-7708 disabled, transfer value > 0" )]
    [TestCase(false, 1ul, 0, TestName = "EIP-7708 disabled, transfer value = 1" )]
    [TestCase(false, 0ul, 0, TestName = "EIP-7708 disabled, transfer value = 0" )]
    public async Task SimpleTransfer_EmitsLogs(bool eip7708Enabled, ulong transferValue, int expectedLogCount)
    {
        OverridableReleaseSpec spec = new(Prague.Instance) { IsEip7708Enabled = eip7708Enabled };
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(b => b.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));

        UInt256 nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithValue(transferValue)
            .WithNonce(nonce)
            .WithGasLimit(21000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(tx);
        TxReceipt[] receipts = chain.ReceiptStorage.Get(block);

        Assert.Multiple(() =>
        {
            Assert.That(receipts, Has.Length.EqualTo(1));
            Assert.That(receipts[0].Logs, Has.Length.EqualTo(expectedLogCount));

            if (expectedLogCount > 0)
            {
                LogEntry log = receipts[0].Logs![0];
                Assert.That(log.Address, Is.EqualTo(TransferLog.Erc20Sender));
                Assert.That(log.Topics, Has.Length.EqualTo(3));
                Assert.That(log.Topics[0], Is.EqualTo(TransferLog.TransferSignature));
                Assert.That(log.Topics[1], Is.EqualTo(TestItem.AddressA.ToHash().ToHash256()));
                Assert.That(log.Topics[2], Is.EqualTo(TestItem.AddressB.ToHash().ToHash256()));
                Assert.That(new UInt256(log.Data, isBigEndian: true), Is.EqualTo((UInt256)transferValue));
            }
        });
    }
}
