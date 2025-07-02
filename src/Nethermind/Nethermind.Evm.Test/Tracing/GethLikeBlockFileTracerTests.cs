// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using NUnit.Framework;
using System.IO.Abstractions.TestingHelpers;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeBlockFileTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Should_have_file_names_matching_block_and_transactions()
    {
        var fileSystem = new MockFileSystem();
        var block = Build.A.Block
            .WithTransactions(new[] {
                Build.A.Transaction.WithHash(Keccak.OfAnEmptyString).TestObject,
                Build.A.Transaction.WithHash(Keccak.OfAnEmptySequenceRlp).TestObject
            })
            .TestObject;

        var tracer = new GethLikeBlockFileTracer(block, GethTraceOptions.Default, fileSystem);
        var blockTracer = (IBlockTracer)tracer;

        for (var i = 0; i < block.Transactions.Length; i++)
        {
            var tx = block.Transactions[i];

            blockTracer.StartNewTxTrace(tx);
            blockTracer.EndTxTrace();

            var fileName = tracer.FileNames.Last();

            fileName.Should().Contain($"block_{block.Hash.Bytes[..4].ToHexString(true)}-{i}-{tx.Hash.Bytes[..4].ToHexString(true)}-");
            fileName.Should().EndWith(".jsonl");
        }
    }
}
