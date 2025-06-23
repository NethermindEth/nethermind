
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using NUnit.Framework;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Evm.Test.ILEVM;


public static class PrimeTestInputs
{
    public static IEnumerable<TestFixtureData> Fixtures => new[]
    {
       // new TestFixtureData(false, new UInt256(23).ToBigEndian().PadLeft(32)),
        new TestFixtureData(false, new UInt256(8000009UL).ToBigEndian().PadLeft(32)),
       // new TestFixtureData(false, new UInt256(16000057UL).ToBigEndian().PadLeft(32)),
       // new TestFixtureData(true, new UInt256(23).ToBigEndian().PadLeft(32)),
        new TestFixtureData(true, new UInt256(8000009UL).ToBigEndian().PadLeft(32)),
       // new TestFixtureData(true, new UInt256(16000057UL).ToBigEndian().PadLeft(32)),
    };
}
[NonParallelizable]
[TestFixture(false)]
[TestFixture(true)]
public class PrimeTest(bool useIlEvm) : RealContractTestsBase(useIlEvm)
{
    const int IsPrime = 1;
    const int NotPrime = 0;
    UInt256 Number = 8000009UL;
    //UInt256 Number = 23UL;

    [SetUp]
    public void SetUp()
    {
        AotContractsRepository.ClearCache();
        Precompiler.ResetEnvironment(true);

        Metrics.IlvmAotPrecompiledCalls = 0;
    }

    // Represents the address
    private static readonly Address SenderAddress = SenderRecipientAndMiner.Default.Sender;
    private static readonly UInt256 PrimeResultStorage = new(0, 0, 0, 0);
    private static readonly StorageCell PrimeBalanceCell = new(ContractAddress, PrimeResultStorage);



    [Test]
    public void TestIsPrime()
    {
        // https://etherscan.io/tx/0x3ab9c62830fb8db708bd5ab23506465c37f589eb6ccaf3ec455a0f4f5ef2c5fd
   //     UInt256 value = 1000;
   //     var paddedValue = value.ToBigEndian().PadLeft(32);

   //     // Arrange value to be equal to the transfer value.
   //     TestState.Set(SenderBalanceCell, value.ToBigEndian().WithoutLeadingZeros().ToArray());

        var primeContractKey = SenderRecipientAndMiner.Default.RecipientKey;
        var miner = SenderRecipientAndMiner.Default.MinerKey;

        TestState.CreateAccount(ContractAddress, 100.Ether());
        var hashcode = Keccak.Compute(ByteCode);
        TestState.InsertCode(ContractAddress, hashcode, ByteCode, SpecProvider.GenesisSpec);
        SenderRecipientAndMiner toPrime = new SenderRecipientAndMiner
        {
            SenderKey = SenderRecipientAndMiner.Default.SenderKey,
            RecipientKey = primeContractKey,
            MinerKey = miner,
        };

        (Block block1, Transaction txAtoB) = PrepareTx(Activation, 1000000, ByteCode, [], 10000, toPrime);

        ExecuteNoPrepare(block1, txAtoB, NullTxTracer.Instance, Activation, 1000000, null, true);

        AssertBalance(PrimeBalanceCell, IsPrime);
    }

    private void AssertBalance(in StorageCell cell, UInt256 expected)
    {
        ReadOnlySpan<byte> read = TestState.Get(cell);
        UInt256 after = new UInt256(read);
        after.Should().Be(expected);
    }
        private static byte[] FibbBytecode(UInt256 number)
        {
            byte[] bytes = new byte[32];
            number.ToBigEndian(bytes);
            var argBytes = bytes.WithoutLeadingZeros().ToArray();
            return  Prepare.EvmCode
                        .JUMPDEST()
                        .PUSHx([0])
                        .POP()
                        .PUSHx(argBytes)
                        .COMMENT("Store variable(n) in Memory")
                        .MSTORE(0)
                        .COMMENT("Store Indexer(i) in Memory")
                        .PushData(2)
                        .MSTORE(32)
                        .COMMENT("We mark this place as a GOTO section")
                        .JUMPDEST()
                        .COMMENT("We check if i * i < n + 1")
                        .MLOAD(32)
                        .DUPx(1)
                        .MUL()
                        .MLOAD(0)
                        .ADD(1) //3
                        .LT()
                        .PushData(4 + 3 +  47 + argBytes.Length)
                        .JUMPI()
                        .COMMENT("We check if n % i == 0")
                        .MLOAD(32)
                        .MLOAD(0)
                        .MOD()
                        .ISZERO()
                        .DUPx(1)
                        .COMMENT("if 0 we jump to the end")
                        .PushData(4 + 3 + 51 + argBytes.Length)
                        .JUMPI()
                        .POP()
                        .COMMENT("increment Indexer(i)")
                        .MLOAD(32)
                        .ADD(1)
                        .MSTORE(32)
                        .COMMENT("Loop back to top of conditional loop")
                        .PushData(4 + 9 + argBytes.Length)
                        .JUMP()
                        .COMMENT("return 0")
                        .JUMPDEST()
                        .PushData(1)
                        .SSTORE(0)
                        .STOP()
                        .JUMPDEST()
                        .SSTORE(0)
                        .STOP()
                        .Done;

        }

    protected override byte[] ByteCode =>  FibbBytecode(Number);
}
