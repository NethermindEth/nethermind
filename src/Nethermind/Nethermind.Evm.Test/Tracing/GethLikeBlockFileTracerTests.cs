// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;
using Testably.Abstractions.Testing;
using Nethermind.Core;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeBlockFileTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Should_have_file_names_matching_block_and_transactions()
    {
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();

        Block block = Build.A.Block
            .WithTransactions(new[] {
                Build.A.Transaction.WithHash(Keccak.OfAnEmptyString).TestObject,
                Build.A.Transaction.WithHash(Keccak.OfAnEmptySequenceRlp).TestObject
            })
            .TestObject;

        GethLikeBlockFileTracer tracer = new(block, GethTraceOptions.Default, fileSystem);
        IBlockTracer blockTracer = (IBlockTracer)tracer;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];

            blockTracer.StartNewTxTrace(tx);
            blockTracer.EndTxTrace();

            string fileName = tracer.FileNames.Last();

            fileName.Should().Contain($"block_{block.Hash.Bytes[..4].ToHexString(true)}-{i}-{tx.Hash.Bytes[..4].ToHexString(true)}-");
            fileName.Should().EndWith(".jsonl");
        }
    }
}
