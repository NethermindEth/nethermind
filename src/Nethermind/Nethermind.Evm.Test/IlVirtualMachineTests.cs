//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class IlVirtualMachineTests : VirtualMachineTestsBase
{
    protected override ILogManager GetLogManager() => new NUnitLogManager();

    [Test]
    public void PC_POP()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .Op(Instruction.PC)
            .Op(Instruction.POP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Jump_Invalid()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.JUMP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.AreEqual(result.Error, EvmExceptionType.InvalidJumpDestination.ToString());
    }

    [Test]
    public void Jump_Valid()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(3)
            .Op(Instruction.JUMP)
            .Op(Instruction.JUMPDEST)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Jumpi_InvalidCondition()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(0) // invalid condition
            .PushData(1) // address
            .Op(Instruction.JUMPI)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Jumpi_ValidCondition_InvalidAddress()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(1) // valid condition
            .PushData(1) // address
            .Op(Instruction.JUMPI)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.AreEqual(result.Error, EvmExceptionType.InvalidJumpDestination.ToString());
    }

    [Test]
    public void Jumpi_ValidCondition_ValidAddress()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(1) // valid condition
            .PushData(5) // address
            .Op(Instruction.JUMPI)
            .Op(Instruction.JUMPDEST)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Sub()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(2)
            .PushData(1)
            .PushData(4)
            .Op(Instruction.SUB)    // 4 - 1 = 3
            .Op(Instruction.SUB)    // 3 - 2 = 1
            .Op(Instruction.SUB)    // 1 - 1 = 0
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Dup()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.DUP1)
            .Op(Instruction.SUB)
            .Op(Instruction.POP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    [Test]
    public void Swap()
    {
        Machine.BuildILForNext();

        byte[] code = Prepare.EvmCode
            .PushData(2)
            .PushData(1)
            .Op(Instruction.SWAP1)
            .Op(Instruction.SUB)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Console.WriteLine(result.Error);
    }

    //[Test]
    //public void OutOfGas()
    //{
    //    // an infinite loop
    //    byte[] code = Prepare.EvmCode
    //        .Op(Instruction.JUMPDEST)
    //        .PushData(0)
    //        .Op(Instruction.JUMP)
    //        .Done;

    //    TestAllTracerWithOutput result = Execute(BlockNumber, 10000, code);

    //    Assert.AreEqual(result.Error, EvmExceptionType.OutOfGas.ToString());
    //}

    [TestCase(true, TestName = "IL")]
    [TestCase(false, TestName = "Interpreter")]
    [Explicit]
    public void Long_Loop(bool isIL)
    {
        if (isIL)
        {
            Machine.BuildILForNext();
        }

        const int loopCount = 200_000;
        byte[] repeat = new byte[4];
        BinaryPrimitives.TryWriteInt32BigEndian(repeat, loopCount);

        Prepare code = Prepare.EvmCode
            .PushData(repeat)
            .Op(Instruction.JUMPDEST) // counter
            .PushData(1) // counter, 1
            .Op(Instruction.SWAP1) // 1, counter
            .Op(Instruction.SUB) // counter-1
            .Op(Instruction.DUP1) // counter-1, counter-1
            .PushData(1 + repeat.Length) // counter-1, counter-1, 2
            .Op(Instruction.JUMPI);      // counter-1

        if (isIL)
        {
            // differentiate by adding one point
            code = code.Op(Instruction.POP);
        }

        // warmup
        Execute(BlockNumber, 10_000, code.Done);

        Stopwatch sw = Stopwatch.StartNew();

        TestAllTracerWithOutput result = Execute(BlockNumber, 7_000_000, code.Done);

        const int n = 1_000;

        Console.WriteLine($"Execution of {loopCount} took {sw.Elapsed} taking {((double)sw.ElapsedMilliseconds * n) / loopCount}ms per {n} spins");
        Console.WriteLine(result.Error);
    }
}
