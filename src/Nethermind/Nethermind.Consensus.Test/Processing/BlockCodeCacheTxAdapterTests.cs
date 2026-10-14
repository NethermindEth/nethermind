// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

public class BlockCodeCacheTxAdapterTests
{
    private const int CodeLength = 1_024;

    [Test]
    public void Past_the_cap_a_transaction_evicts_the_code_of_one_that_finished()
    {
        // Room for one code only.
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 2 * CodeLength);
        CodeInfo[] codes = [new(new byte[CodeLength]), new(new byte[CodeLength])];
        ITransactionProcessorAdapter inner = Substitute.For<ITransactionProcessorAdapter>();
        inner.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(call =>
        {
            int index = (int)call.Arg<Transaction>().Nonce;
            ValueHash256 hash = Hash(index);
            cache.Set(in hash, codes[index]);
            return TransactionResult.Ok;
        });
        BlockCodeCacheTxAdapter adapter = new(inner, cache);

        adapter.Execute(Build.A.Transaction.WithNonce(0).TestObject, NullTxTracer.Instance);
        adapter.Execute(Build.A.Transaction.WithNonce(1).TestObject, NullTxTracer.Instance);

        ValueHash256 first = Hash(0);
        ValueHash256 second = Hash(1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Get(in first), Is.Null);
            Assert.That(cache.Get(in second), Is.SameAs(codes[1]));
        }
    }

    private static ValueHash256 Hash(int i) => ValueKeccak.Compute([(byte)i]);
}
