// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>KECCAK256 results checked against direct hashing, for a cache miss and the cache hit that follows.</summary>
[Parallelizable(ParallelScope.Self)]
[TestFixture(false)]
[TestFixture(true)]
public class Keccak256ResultTests(bool tracing) : VirtualMachineTestsBase
{
    protected override TestAllTracerWithOutput CreateTracer() => new StackPushTracer(tracing);

    private sealed class StackPushTracer(bool tracing) : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => tracing;
        public List<byte[]> Pushes { get; } = [];
        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => Pushes.Add(stackItem.ToArray());
    }

    [Test]
    public void Matches_direct_hash_on_miss_and_hit(
        [Values(0, 1, 19, 20, 31, 32, 33, 63, 64, 65, 92, 93, 135, 136, 137, 200)] int length,
        [Values(0, 7, 32)] int offset)
    {
        // Bytes past the hashed range are random too, so reading beyond the length changes the result.
        byte[] data = new byte[length + 40];
        new Random(length * 64 + offset).NextBytes(data);
        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(offset, data)
            .PushData(length).PushData(offset).Op(Instruction.KECCAK256).PushData(0).Op(Instruction.SSTORE)
            .PushData(length).PushData(offset).Op(Instruction.KECCAK256).PushData(1).Op(Instruction.SSTORE)
            .Done;

        TestAllTracerWithOutput receipt = Execute(Activation, 1_000_000, code);

        byte[] expected = ValueKeccak.Compute(data.AsSpan(0, length)).ToByteArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertStorage((UInt256)0, (ReadOnlySpan<byte>)expected);
            AssertStorage((UInt256)1, (ReadOnlySpan<byte>)expected);
            if (tracing)
            {
                Assert.That(((StackPushTracer)receipt).Pushes.FindAll(push => push.AsSpan().SequenceEqual(expected)), Has.Count.EqualTo(2));
            }
        }
    }
}
