//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

public class ReceiptsRecoveryTests
{
    private IReceiptsRecovery _receiptsRecovery;
    
    [SetUp]
    public void Setup()
    {
        RopstenSpecProvider specProvider = RopstenSpecProvider.Instance;
        EthereumEcdsa ethereumEcdsa = new(specProvider.ChainId, LimboLogs.Instance);

        _receiptsRecovery = new ReceiptsRecovery(ethereumEcdsa, specProvider);
    }

    [TestCase(5, 5, true, ReceiptsRecoveryResult.Success)]
    [TestCase(5, 5, false, ReceiptsRecoveryResult.Skipped)]
    [TestCase(0, 0, true, ReceiptsRecoveryResult.Skipped)]
    [TestCase(1, 0, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(0, 1, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(5, 4, true, ReceiptsRecoveryResult.Fail)]
    [TestCase(1, 2, true, ReceiptsRecoveryResult.Fail)]
    public void TryRecover_should_return_correct_receipts_recovery_result(int blockTxsLength, int receiptsLength, bool forceRecoverSender, ReceiptsRecoveryResult expected)
    {
        Transaction[] txs = new Transaction[blockTxsLength];
        for (int i = 0; i < blockTxsLength; i++)
        {
            txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
        }

        Block block = Build.A.Block.WithTransactions(txs).TestObject;

        TxReceipt[] receipts = new TxReceipt[receiptsLength];
        for (int i = 0; i < receiptsLength; i++)
        {
            receipts[i] = Build.A.Receipt.WithBlockHash(block.Hash).TestObject;
        }

        _receiptsRecovery.TryRecover(block, receipts, forceRecoverSender).Should().Be(expected);
    }
}
