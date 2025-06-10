// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core.Crypto;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Threading;
using Org.BouncyCastle.Asn1.X509;

namespace Nethermind.Evm.Test.ILEVM
{
    [TestFixture]
    [NonParallelizable]
    internal class IlEvmTests
    {
        [SetUp]
        public void Init()
        {
            AotContractsRepository.ClearCache();
            Precompiler.ResetEnvironment(true);

            Metrics.IlvmAotPrecompiledCalls = 0;
        }

        private const int RepeatCount = 256;
        public static IEnumerable<(string, Instruction[], byte[], EvmExceptionType, bool, IReleaseSpec)> GetJitBytecodesSamples()
        {
            IEnumerable<(Instruction[], byte[], EvmExceptionType, bool)> GetJitBytecodesSamplesGenerator(bool turnOnAggressiveMode)
            {
                yield return ([Instruction.PUSH32], Prepare.EvmCode
                        .PUSHx([1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1])
                        .PushSingle(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ISZERO], Prepare.EvmCode
                        .ISZERO(7)
                        .PushData(7)
                        .SSTORE()
                        .ISZERO(0)
                        .PushData(1)
                        .SSTORE()
                        .ISZERO(UInt256.MaxValue)
                        .PushData(23)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SUB], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .SUB()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(UInt256.MaxValue)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(7)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.ADD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .ADD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .PushSingle(5)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(0)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(1)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .PushSingle(23)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDMOD], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(7)
                        .PushSingle(23)
                        .ADDMOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue / 2)
                        .PushSingle(2)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(2)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MUL], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .MUL()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(2)
                        .PushSingle(255)
                        .EXP()
                        .PushData(2)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .EXP()
                        .PushData(2)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .EXP()
                        .PushData(3)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(0)
                        .EXP()
                        .PushData(4)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXP], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(1)
                        .EXP()
                        .PushData(5)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(1)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(0)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MOD], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(7)
                        .MOD()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(1)
                        .PushSingle(0)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(0)
                        .PushSingle(1)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(UInt256.MaxValue)
                        .PushSingle(100000)
                        .DIV()
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.DIV], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .DIV()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE, Instruction.MLOAD], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(0, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(123, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(32, ((UInt256)23).PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(0, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(0)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(123, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(123)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSTORE8], Prepare.EvmCode
                        .MSTORE8(32, UInt256.MaxValue.PaddedBytes(32))
                        .MLOAD(32)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 0, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(0, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 0, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(123, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 123, 32)
                        .MLOAD(32)
                        .MLOAD(123)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(0, 32, 32)
                        .MLOAD(32)
                        .MLOAD(0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)0).PaddedBytes(32))
                        .MCOPY(32, 32, 32)
                        .MLOAD(32)
                        .PushSingle((UInt256)0)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MCOPY], Prepare.EvmCode
                        .MSTORE(32, ((UInt256)23).PaddedBytes(32))
                        .MCOPY(32, 32, 32)
                        .MLOAD(32)
                        .PushSingle((UInt256)23)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EQ], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(23)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.EQ], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .EQ()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(23)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GT], Prepare.EvmCode
                        .PushSingle(17)
                        .PushSingle(17)
                        .GT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(23)
                        .PushSingle(7)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(7)
                        .PushSingle(23)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LT], Prepare.EvmCode
                        .PushSingle(17)
                        .PushSingle(17)
                        .LT()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.NOT], Prepare.EvmCode
                        .PushSingle(1)
                        .NOT()
                        .PushData(1)
                        .SSTORE()
                        .PushSingle(0)
                        .NOT()
                        .PushData(2)
                        .SSTORE()
                        .PushSingle(1024)
                        .NOT()
                        .PushData(3)
                        .SSTORE()
                        .PushSingle(UInt256.MaxValue)
                        .NOT()
                        .PushData(4)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(11)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(9)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBHASH], Prepare.EvmCode
                        .PushSingle(0)
                        .BLOBHASH()
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOCKHASH], Prepare.EvmCode
                    .BLOCKHASH(UInt256.MaxValue - 1000)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOCKHASH], Prepare.EvmCode
                    .BLOCKHASH(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(2, 2, 10) //dest, src, len
                    .MLOAD(2)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(0, 30, 2)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CALLDATACOPY], Prepare.EvmCode
                    .CALLDATACOPY(0, 0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATALOAD], Prepare.EvmCode
                    .CALLDATALOAD(16)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATALOAD], Prepare.EvmCode
                    .CALLDATALOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MSIZE], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .MSIZE()
                    .PushData(1)
                    .SSTORE()
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(32)
                    .Op(Instruction.MSTORE)
                    .MSIZE()
                    .PushData(2)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.MSIZE], Prepare.EvmCode
                    .MSIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GASPRICE], Prepare.EvmCode
                    .GASPRICE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CODESIZE], Prepare.EvmCode
                    .CODESIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.PC], Prepare.EvmCode
                    .PC()
                    .PushData(1)
                    .SSTORE()
                    .PC()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.COINBASE], Prepare.EvmCode
                    .COINBASE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.TIMESTAMP], Prepare.EvmCode
                    .TIMESTAMP()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.NUMBER], Prepare.EvmCode
                    .NUMBER()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GASLIMIT], Prepare.EvmCode
                    .GASLIMIT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLER], Prepare.EvmCode
                    .CALLER()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ADDRESS], Prepare.EvmCode
                    .ADDRESS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.ORIGIN], Prepare.EvmCode
                    .ORIGIN()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLVALUE], Prepare.EvmCode
                    .CALLVALUE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CHAINID], Prepare.EvmCode
                    .CHAINID()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.GAS], Prepare.EvmCode
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .PushData(23)
                    .PushData(46)
                    .ADD()
                    .POP()
                    .GAS()
                    .PushData(2)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATASIZE], Prepare.EvmCode
                    .RETURNDATASIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BASEFEE], Prepare.EvmCode
                    .BASEFEE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURN], Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .RETURN(0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE().Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.REVERT], Prepare.EvmCode
                    .StoreDataInMemory(0, [2, 3, 5, 7])
                    .REVERT(0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLDATASIZE], Prepare.EvmCode
                    .CALLDATASIZE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMPI, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);


                yield return ([Instruction.JUMPI, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .JUMPI(9)
                    .PushSingle(3)
                    .JUMPDEST()
                    .PushSingle(0)
                    .MUL()
                    .GAS()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMP, Instruction.JUMPDEST], Prepare.EvmCode
                    .PushSingle(23)
                    .JUMP(14)
                    .JUMPDEST()
                    .PushSingle(3)
                    .MUL()
                    .GAS()
                    .PUSHx([1])
                    .SSTORE()
                    .STOP()
                    .JUMPDEST()
                    .JUMP(5)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.JUMPDEST], Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(3)
                    .PushSingle(3)
                    .MUL()
                    .GAS()
                    .PUSHx([1])
                    .SSTORE()
                    .STOP()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);



                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(255)
                    .PushSingle(10)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHL], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(32)
                    .SHL()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(255)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(256)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SHR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(32)
                    .SHR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushSingle(0)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushSingle(1)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(0)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(23)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(17)
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle((UInt256)((Int256.Int256)(-1)))
                    .SAR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SAR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle((UInt256)((Int256.Int256)(-1)))
                    .SAR()
                    .PushSingle((UInt256)((Int256.Int256)(1)))
                    .SAR()
                    .PushSingle(23)
                    .EQ()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(UInt256.MaxValue)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.AND], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(0)
                    .AND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(0)
                    .PushSingle(UInt256.MaxValue)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.OR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(0)
                    .OR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue)
                    .PushSingle(1023)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(255)
                    .PushSingle(3)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.XOR], Prepare.EvmCode
                    .PushSingle(23)
                    .PushSingle(1)
                    .XOR()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(UInt256.MaxValue - 1)
                    .PushSingle(UInt256.MaxValue - 2)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushData(UInt256.MaxValue - 1)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushSingle(17)
                    .PushData(23)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(23)
                    .PushSingle(17)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SLT], Prepare.EvmCode
                    .PushData(17)
                    .PushSingle(17)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(23)
                    .PushData(17)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(UInt256.MaxValue - 1)
                    .PushSingle(UInt256.MaxValue - 2)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushSingle(UInt256.MaxValue - 2)
                    .PushData(UInt256.MaxValue - 1)
                    .SLT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(17)
                    .PushData(17)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SGT], Prepare.EvmCode
                    .PushData(17)
                    .PushData(23)
                    .SGT()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(0, ((UInt256)(23)).PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(31, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(0, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(16, UInt256.MaxValue.PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BYTE], Prepare.EvmCode
                    .BYTE(16, ((UInt256)(23)).PaddedBytes(32))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG0], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .LOGx(0, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG1], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .LOGx(1, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG2], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .LOGx(2, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG3], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .PushData(TestItem.KeccakC.Bytes.ToArray())
                    .LOGx(3, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.LOG4], Prepare.EvmCode
                    .PushData(IlVirtualMachineTestsBase.SampleHexData1.PadLeft(64, '0'))
                    .PushData(0)
                    .Op(Instruction.MSTORE)
                    .PushData(TestItem.KeccakA.Bytes.ToArray())
                    .PushData(TestItem.KeccakB.Bytes.ToArray())
                    .PushData(TestItem.KeccakC.Bytes.ToArray())
                    .PushData(TestItem.KeccakD.Bytes.ToArray())
                    .LOGx(4, 0, 64)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.TSTORE, Instruction.TLOAD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .TSTORE()
                    .PushData(7)
                    .TLOAD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SSTORE, Instruction.SLOAD], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(UInt256.MaxValue)
                    .SSTORE()
                    .PushData(UInt256.MaxValue)
                    .SLOAD()
                    .PushData(1)
                    .SSTORE()
                .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SSTORE, Instruction.SLOAD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SSTORE()
                    .PushData(7)
                    .SLOAD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODESIZE], Prepare.EvmCode
                    .EXTCODESIZE(Address.FromNumber(23)) // Cold Access
                    .EXTCODESIZE(Address.FromNumber(23)) // Warm Access
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODESIZE], Prepare.EvmCode
                    .EXTCODESIZE(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODEHASH], Prepare.EvmCode
                    .EXTCODEHASH(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODECOPY], Prepare.EvmCode
                    .EXTCODECOPY(Address.FromNumber(23), 0, 5, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .EXTCODECOPY(Address.FromNumber(23), 0, 5, 32) // warm
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.EXTCODECOPY], Prepare.EvmCode
                    .EXTCODECOPY(Address.FromNumber(23), 0, 0, 32)
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BALANCE], Prepare.EvmCode
                    .BALANCE(Address.FromNumber(23))
                    .PushData(1)
                    .SSTORE()
                    .BALANCE(Address.FromNumber(23)) // warm access
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SELFBALANCE], Prepare.EvmCode
                    .SELFBALANCE()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .INVALID()
                    .PushData(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.BadInstruction, turnOnAggressiveMode);

                yield return ([Instruction.STOP], Prepare.EvmCode
                    .STOP()
                    .PushData(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.POP], Prepare.EvmCode
                    .PUSHx()
                    .POP()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.POP], Prepare.EvmCode
                    .POP()
                    .POP()
                    .POP()
                    .POP()
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);

                yield return ([Instruction.POP], Prepare.EvmCode
                    .PushData(23)
                    .PushData(23)
                    .Op((Instruction)0x2c) // an unused opcode should be here
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);

                yield return ([Instruction.SSTORE], Prepare.EvmCode
                    .PUSHx()
                    .DUPx(1)
                    .SSTORE()
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);


                for (byte opcode = (byte)Instruction.DUP1; opcode <= (byte)Instruction.DUP16; opcode++)
                {
                    int n = opcode - (byte)Instruction.DUP1 + 1;
                    var test = Prepare.EvmCode;
                    for (int i = 0; i < n; i++)
                    {
                        test.PushData(i);
                    }
                    test.Op((Instruction)opcode)
                        .PushData(1)
                        .SSTORE();

                    yield return ([(Instruction)opcode], test.Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                for (byte opcode = (byte)Instruction.PUSH0; opcode <= (byte)Instruction.PUSH32; opcode++)
                {
                    int n = opcode - (byte)Instruction.PUSH0;
                    byte[] args = n == 0 ? null : Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

                    yield return ([(Instruction)opcode], Prepare.EvmCode.PUSHx(args)
                        .PushData(1)
                        .SSTORE()
                        .Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                for (byte opcode = (byte)Instruction.SWAP1; opcode <= (byte)Instruction.SWAP16; opcode++)
                {
                    int n = opcode - (byte)Instruction.SWAP1 + 2;
                    var test = Prepare.EvmCode;
                    for (int i = 0; i < n; i++)
                    {
                        test.PushData(i);
                    }
                    test.Op((Instruction)opcode)
                        .PushData(1)
                        .SSTORE();

                    yield return ([(Instruction)opcode], test.Done, EvmExceptionType.None, turnOnAggressiveMode);
                }

                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData((UInt256)new Int256.Int256(-23))
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SDIV], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SDIV()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(0)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .PushData(7)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData(VirtualMachine.P255)
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SMOD], Prepare.EvmCode
                    .PushData((UInt256)Int256.Int256.MinusOne)
                    .PushData(VirtualMachine.P255)
                    .SMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(100)  // size
                    .PushData(3) // code start idx
                    .PushData(2) // memory start idx
                    .CODECOPY()
                    .MLOAD(2)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(32)  // size
                    .PushData(0) // code start idx
                    .PushData(0) // memory start idx
                    .CODECOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.CODECOPY], Prepare.EvmCode
                    .PushData(0)
                    .PushData(32)
                    .PushData(7)
                    .CODECOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(10)
                    .PushData(10)
                    .PushData(1)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(0)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .PushData(1)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(0)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(1)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(0)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(1)
                    .PushData(3)
                    .PushData(7)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(23)
                    .PushData(7)
                    .PushData(23)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.MULMOD], Prepare.EvmCode
                    .PushData(7)
                    .PushData(7)
                    .PushData(23)
                    .MULMOD()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 31).Select(i => (byte)i).ToArray())
                    .PushData(32) // size
                    .PushData(0) // mem start idx
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                    .PushData(16) // size
                    .PushData(16) // mem start idx
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.KECCAK256], Prepare.EvmCode
                    .MSTORE(0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray())
                    .PushData(0)
                    .PushData(16)
                    .KECCAK256()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.PREVRANDAO], Prepare.EvmCode
                    .PREVRANDAO()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATACOPY], Prepare.EvmCode
                    .Call(Address.FromNumber((int)Instruction.RETURNDATASIZE), 10000)
                    .PushData(32) // size
                    .PushData(0) // data idx
                    .PushData(0) // mem idx
                    .RETURNDATACOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.RETURNDATACOPY], Prepare.EvmCode
                    .PushData(0)
                    .PushData(32)
                    .PushData(0)
                    .RETURNDATACOPY()
                    .MLOAD(0)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.BLOBBASEFEE], Prepare.EvmCode
                    .Op(Instruction.BLOBBASEFEE)
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(65525)
                    .PushData(1)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);
                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(1023)
                    .PushData(0)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(1024)
                    .PushData(16)
                    .SIGNEXTEND()
                    .PushData(1)
                    .SSTORE()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(255)
                    .PushData(0)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(255)
                    .PushData(32)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SIGNEXTEND], Prepare.EvmCode
                    .PushData(UInt256.MaxValue)
                    .PushData(31)
                    .Op(Instruction.SIGNEXTEND)
                    .PushData(0)
                    .Op(Instruction.SSTORE)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.SELFDESTRUCT], Prepare.EvmCode
                    .PushData(23)
                    .PushData(3)
                    .MUL()
                    .PushData(123)
                    .SSTORE()
                    .PushData(Address.Zero)
                    .SELFDESTRUCT()
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALL], Prepare.EvmCode
                    .Call(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.DELEGATECALL], Prepare.EvmCode
                    .DelegateCall(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.STATICCALL], Prepare.EvmCode
                    .StaticCall(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CALLCODE], Prepare.EvmCode
                    .CallCode(Address.FromNumber((int)Instruction.RETURN), 10000)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CREATE], Prepare.EvmCode
                    .Create(Prepare.EvmCode.STOP().Done, 0)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.CREATE2], Prepare.EvmCode
                    .Create2(Prepare.EvmCode.STOP().Done, [1, 2, 3], 0)
                    .Done, EvmExceptionType.None, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .JUMPDEST()
                    .MUL(23, 3)
                    .POP()
                    .JUMP(0)
                    .Done, EvmExceptionType.OutOfGas, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .JUMPDEST()
                    .PUSHx()
                    .DUPx(1)
                    .DUPx(1)
                    .DUPx(1)
                    .DUPx(1)
                    .DUPx(1)
                    .DUPx(1)
                    .DUPx(1)
                    .JUMP(0)
                    .Done, EvmExceptionType.StackOverflow, turnOnAggressiveMode);

                yield return ([Instruction.INVALID], Prepare.EvmCode
                    .JUMPDEST()
                    .MUL(23)
                    .JUMP(0)
                    .Done, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);


                long maxSize = 24.KiB();

                byte[] bytecode = new byte[maxSize];
                int index = 0;


                byte[] segment = Prepare.EvmCode
                    .PushData(1)
                    .PushData(1)
                    .ADD()
                    .PushData(1)
                    .SSTORE()
                    .Done;

                while (index + segment.Length < bytecode.Length)
                {
                    Array.Copy(
                        segment, 0,
                        bytecode, index, segment.Length
                        );

                    index += segment.Length;
                }

                yield return ([Instruction.ADD | Instruction.SSTORE | Instruction.INVALID], bytecode, EvmExceptionType.StackUnderflow, turnOnAggressiveMode);

            }

            /*static IEnumerable<IReleaseSpec> GetAllFroksStarting<T>()
            {
                var baseType = typeof(T);
                var assembly = Assembly.GetAssembly(baseType);

                return assembly.GetTypes()
                                .Where(t => t != baseType && baseType.IsAssignableFrom(t) && !t.IsAbstract)
                                .Select(t => (IReleaseSpec)t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetMethod.Invoke(null, []));
            }
            */
            IEnumerable<IReleaseSpec> forks = [Cancun.Instance]; //GetAllFroksStarting<Olympic>();

            bool[] combinations = new[]
            {
                true, false
            };

            foreach (var combination in combinations)
            {
                foreach (var sample in GetJitBytecodesSamplesGenerator(combination))
                {
                    foreach (var fork in forks)
                    {
                        yield return new($"[{String.Join(", ", sample.Item1.Select(op => op.ToString()))}]", sample.Item1, sample.Item2, sample.Item3, sample.Item4, fork);
                    }
                }
            }
        }

        [Test]
        public void All_Opcodes_Are_Covered_in_JIT_Tests()
        {
            List<Instruction> instructions = System.Enum.GetValues<Instruction>().ToList();

            var tests = GetJitBytecodesSamples()
                .SelectMany(test => test.Item2)
                .ToHashSet();

            List<Instruction> notCovered = new List<Instruction>();
            foreach (var opcode in instructions)
            {
                if (!tests.Contains(opcode))
                {
                    notCovered.Add(opcode);
                }
            }

            Assert.That(notCovered.Count, Is.EqualTo(0), $"[{String.Join(", ", notCovered)}]");
        }

        [Test]
        public void Execution_Swap_Happens_When_Compilation_Occurs()
        {
            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
            {
                IsILEvmEnabled = true,
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
            }, Prague.Instance);

            byte[] bytecode =
                Prepare.EvmCode
                    .JUMPDEST()
                    .PushSingle(1000)
                    .GAS()
                    .LT()
                    .PUSHx([0, 26])
                    .JUMPI()
                    .PushSingle(23)
                    .PushSingle(7)
                    .ADD()
                    .POP()
                    .PushSingle(42)
                    .PushSingle(5)
                    .ADD()
                    .POP()
                    .PUSHx([0, 0])
                    .JUMP()
                    .JUMPDEST()
                    .STOP()
                    .Done;

            ValueHash256 codehash = Keccak.Compute(bytecode);

            for (int i = 0; i < RepeatCount; i++)
            {
                enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, forceAnalysis: false);
            }

            Assert.That(AotContractsRepository.TryGetIledCode(codehash, out var iledCode), Is.True);
            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
        }

        [Test]
        public void All_Opcodes_Have_Metadata()
        {
            Instruction[] instructions = System.Enum.GetValues<Instruction>();
            foreach (var opcode in instructions)
            {
                Assert.That(OpcodeMetadata.Operations.ContainsKey(opcode), Is.True);
            }
        }

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void ILVM_AOT_Execution_Equivalence_Tests((string msg, Instruction[] opcode, byte[] bytecode, EvmExceptionType, bool enableAggressiveMode, IReleaseSpec spec) testcase)
        {
            IlVirtualMachineTestsBase standardChain = new IlVirtualMachineTestsBase(new VMConfig(), testcase.spec);
            Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContracts.dll");

            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
            {
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 256,
                IlEvmAnalysisQueueMaxSize = 256,
                IsIlEvmAggressiveModeEnabled = testcase.enableAggressiveMode,
                IlEvmPersistPrecompiledContractsOnDisk = false,
            }, testcase.spec);


            byte[][] blobVersionedHashes = null;
            switch (testcase.opcode[0])
            {
                case Instruction.BLOBHASH:
                    var blobhashesCount = 10;
                    blobVersionedHashes = new byte[blobhashesCount][];
                    for (int i = 0; i < blobhashesCount; i++)
                    {
                        blobVersionedHashes[i] = new byte[32];
                        for (int n = 0; n < blobhashesCount; n++)
                        {
                            blobVersionedHashes[i][n] = (byte)((i * 3 + 10 * 7) % 256);
                        }
                    }
                    break;
                case Instruction.RETURNDATACOPY:
                    var returningCode = Prepare.EvmCode
                        .PushData(UInt256.MaxValue)
                        .PUSHx([0])
                        .MSTORE()
                        .Return(32, 0)
                        .Done;
                    var callAddress = standardChain.InsertCode(returningCode);
                    enhancedChain.InsertCode(returningCode);
                    enhancedChain.ForceRunAnalysis(callAddress, ILMode.DYNAMIC_AOT_MODE);

                    var callCode =
                        Prepare.EvmCode
                            .Call(callAddress, 10000)
                            .Done;
                    testcase.bytecode = Bytes.Concat(callCode, testcase.bytecode);
                    break;
                default:
                    break;

            }

            var address = standardChain.InsertCode(testcase.bytecode);
            enhancedChain.InsertCode(testcase.bytecode);

            var bytecode = Prepare.EvmCode
                .PushData(UInt256.MaxValue)
                .PUSHx([2])
                .MSTORE()
                .PushData(0)
                .PushData(0)
                .PushData(32) // length
                .PushData(2) // offset
                .PushData(0) // value
                .PushData(address)
                .PushData(50_000)
                .Op(Instruction.CALL)
                .STOP()
                .Done;

            standardChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);

            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.EqualTo(0));

            enhancedChain.Execute<ITxTracer>(bytecode, NullTxTracer.Instance, blobVersionedHashes: blobVersionedHashes);

            var actual = standardChain.StateRoot;
            var expected = enhancedChain.StateRoot;

            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.GreaterThan(0));
            Assert.That(actual, Is.EqualTo(expected), testcase.msg);
        }

        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void ILVM_AOT_Storage_Roundtrip((string msg, Instruction[] opcode, byte[] bytecode, EvmExceptionType, bool enableAggressiveMode, IReleaseSpec spec) testcase)
        {
            String path = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContractsTests");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
            {
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IlEvmContractsPerDllCount = 1,
                IsIlEvmAggressiveModeEnabled = true,
                IlEvmPersistPrecompiledContractsOnDisk = true,
                IlEvmPrecompiledContractsPath = path,
            }, Prague.Instance);

            string fileName = Precompiler.GetTargetFileName();

            var address = enhancedChain.InsertCode(testcase.bytecode);

            enhancedChain.ForceRunAnalysis(address, ILMode.DYNAMIC_AOT_MODE);

            var assemblyPath = Path.Combine(path, fileName);

            Assembly assembly = Assembly.LoadFile(assemblyPath);
            MethodInfo method = assembly
                .GetTypes()
                .First(type => type.CustomAttributes.Any(attr => attr.AttributeType == typeof(NethermindPrecompileAttribute)))
                .GetMethod(nameof(ILExecutionStep));
            Assert.That(method, Is.Not.Null);
        }


        [Test, TestCaseSource(nameof(GetJitBytecodesSamples))]
        public void ILVM_Attribute_is_Correctly_Attached((string msg, Instruction[] opcode, byte[] bytecode, EvmExceptionType, bool enableAggressiveMode, IReleaseSpec spec) testcase)
        {
            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
            {
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IlEvmContractsPerDllCount = 1,
                IsIlEvmAggressiveModeEnabled = true,
            }, Prague.Instance);

            var address = enhancedChain.InsertCode(testcase.bytecode);

            enhancedChain.ForceRunAnalysis(address, ILMode.DYNAMIC_AOT_MODE);

            var hashcode = Keccak.Compute(testcase.bytecode);

            AotContractsRepository.TryGetIledCode(hashcode, out var iledCode);

            Assert.That(iledCode, Is.Not.Null, "ILVM AOT code is not found in the repository");

            var attributes = iledCode.Method.DeclaringType.GetCustomAttributes(typeof(NethermindPrecompileAttribute), false);

            Assert.That(attributes.Length, Is.EqualTo(1), "ILVM AOT code does not have NethermindPrecompileAttribute");
        }

        // just examples
        // fill in the addresses here and the Playground test will fetch and compile and bundle all of them in one DLL
        // we can modify the config in the test to control bundle sizes 
        public static IEnumerable<string> GetTargetAddress()
        {
            string stats = "{\"initialBlockNumber\":22609231,\"initial_block_date_time\":\"06/01/2025 12:23:11\",\"currentBlockNumber\":22615876,\"current_block_date_time\":\"06/02/2025 10:41:59\",\"stats\":[{\"address\":\"0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2\",\"code_hash\":\"0xd0a06b12ac47863b5c7be4185c2deaad1c61557033f56c7d4ea74429cbb25e23\",\"code_size\":3124,\"count\":115273},{\"address\":\"0x43506849d7c04f9138d1a2050bbf3a0c054402dd\",\"code_hash\":\"0xcdfb7d322961af3acae7a8f7ee8b69c205b36f576cc5b077f170c7eb8ecbe3ea\",\"code_size\":23464,\"count\":41561},{\"address\":\"0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48\",\"code_hash\":\"0xd80d4b7c890cb9d6a4893e6b52bc34b56b25335cb13716e0d1d31383e6b41505\",\"code_size\":2186,\"count\":41488},{\"address\":\"0xdac17f958d2ee523a2206206994597c13d831ec7\",\"code_hash\":\"0xb44fb4e949d0f78f87f79ee46428f23a2a5713ce6fc6e0beb3dda78c2ac1ea55\",\"code_size\":11075,\"count\":34357},{\"address\":\"0x5418226af9c8d5d287a78fbbbcd337b86ec07d61\",\"code_hash\":\"0xc503bc0bd84a50c18853a91fc586a484f9e3e378ef42cbdb58dc00c6010392a6\",\"code_size\":20363,\"count\":14560},{\"address\":\"0x000000000004444c5dc75cb358380d2e3de08a90\",\"code_hash\":\"0x785f1014552b7ce7d5fb7d0c970ca60edee94fd00425d7ca21609acac7ce1293\",\"code_size\":24009,\"count\":9961},{\"address\":\"0x7a250d5630b4cf539739df2c5dacb4c659f2488d\",\"code_hash\":\"0xa324bc7db3d091b6f1a2d526e48a9c7039e03b3cc35f7d44b15ac7a1544c11d2\",\"code_size\":21943,\"count\":7750},{\"address\":\"0x0000000000001ff3684f28c67538d4d072c22734\",\"code_hash\":\"0x99f5e8edaceacfdd183eb5f1da8a7757b322495b80cf7928db289a1b1a09f799\",\"code_size\":1009,\"count\":7727},{\"address\":\"0x66a9893cc07d91d95644aedd05d03f95e1dba8af\",\"code_hash\":\"0x6a5f46971b50c6e1b7eef97902311444e479d734e4f80ad88367783cf373fe7f\",\"code_size\":19499,\"count\":7666},{\"address\":\"0xeeeeee9ec4769a09a76a83c7bc42b185872860ee\",\"code_hash\":\"0x3270015b4cefce79b97dd1e9c491e68509739fc3dee07a5d1dcbed1d94a0fe24\",\"code_size\":4916,\"count\":7322},{\"address\":\"0xbbbbbbbbbb9cc5e90e3b3af64bdaf62c37eeffcb\",\"code_hash\":\"0xfa259fa317198f88f5fa3c119f06c066295dbcd47d715e0a30e1bcf94c02ef8c\",\"code_size\":15623,\"count\":7243},{\"address\":\"0x5141b82f5ffda4c6fe1e372978f1c5427640a190\",\"code_hash\":\"0x193b6746e6e7dc603a01d0484ffab5391a479d24f85c5095a683ee9f001c4669\",\"code_size\":17346,\"count\":6650},{\"address\":\"0x000000000022d473030f116ddee9f6b43ac78ba3\",\"code_hash\":\"0xc67d1657868aa5146eaf24fb879fb1fdec3d2d493b3683a61c9c2f4fb2851131\",\"code_size\":9152,\"count\":6235},{\"address\":\"0xf70da97812cb96acdf810712aa562db8dfa3dbef\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":5551},{\"address\":\"0x774eaf7a53471628768dc679da945847d34b9a55\",\"code_hash\":\"0x62dd439514a5552a8ef8fb9142ebc272898a0eaadfc17382ed6af6ba2eb1c4cd\",\"code_size\":8507,\"count\":4751},{\"address\":\"0x4bba9b6b49f3dfa6615f079e9d66b0aa68b04a4d\",\"code_hash\":\"0xaaabea7c67ca1f87004c2f444b757ce2e3831e4b835e0a057f6af0cfc0c59bfa\",\"code_size\":608,\"count\":4179},{\"address\":\"0x9cdf242ef7975d8c68d5c1f5b6905801699b1940\",\"code_hash\":\"0x5e4dcb0bb1910f6429e5fe91678990088a51c6d1cfe1b31d05fb9d948cc7867c\",\"code_size\":708,\"count\":4070},{\"address\":\"0x0e4cb807edff6926a3b5ea4c34fb0e04ab179e6c\",\"code_hash\":\"0x5b45e874224017091dac8549dda0713b2fbed39b6487c2cfecc564456150eca4\",\"code_size\":11842,\"count\":4070},{\"address\":\"0x1385cfe2ac49c92c28e63e51f9fcdcc06f93ed09\",\"code_hash\":\"0xf521219de13768dde5472043e52d74ca0a32f331728867c1670ee8bb03234440\",\"code_size\":6335,\"count\":3780},{\"address\":\"0x4d4574f50dd8b9dbe623cf329dcc78d76935e610\",\"code_hash\":\"0x8bf3f1579a3088d915006dc8920fda246595ef64dbb3b7dd7d95fc2d39eba056\",\"code_size\":6883,\"count\":2817},{\"address\":\"0x2260fac5e5542a773aa44fbcfedf7c193bc2c599\",\"code_hash\":\"0x131ff5c755b710d543ea70fede2eb38e5d15b1456df0ae932ba12e2786f7e5df\",\"code_size\":4582,\"count\":2770},{\"address\":\"0x1111111254eeb25477b68fb85ed929f73a960582\",\"code_hash\":\"0xb21184893bb5b89a85468883070702045d9821b62ea3b28d3e84d89ab84fd23e\",\"code_size\":22484,\"count\":2559},{\"address\":\"0x6088d94c5a40cecd3ae2d4e0710ca687b91c61d0\",\"code_hash\":\"0xe27acc7680bb1bd825401932d9ecc8d7aae6f8d47c136112e391f664cb53d879\",\"code_size\":21805,\"count\":2511},{\"address\":\"0xb0c41c08fd2f8782c6944204af085fec9db66c56\",\"code_hash\":\"0x3f0880456b39440b71cc7be97625f0585cae70fe457080b99ced8f9ab90d5f67\",\"code_size\":7972,\"count\":2497},{\"address\":\"0x06450dee7fd2fb8e39061434babcfc05599a6fb8\",\"code_hash\":\"0xa17eb6bd9e3366bb4447a294ca48983c15b59e2d43aafb0fea0de025d00eefad\",\"code_size\":10386,\"count\":2240},{\"address\":\"0x111111125421ca6dc452d289314280a0f8842a65\",\"code_hash\":\"0xa5a286be4b80006cc547d7e899871aa01a0e0551e2a509233375405f92098c2f\",\"code_size\":24294,\"count\":2212},{\"address\":\"0xe0e0e08a6a4b9dc7bd67bcb7aade5cf48157d444\",\"code_hash\":\"0xf3d28accb94c26334e15376aa2b13b0e5bb57b69b207e08f4538ae9884d5633b\",\"code_size\":23750,\"count\":2152},{\"address\":\"0x81987681443c156f881b70875724cc78b08ada26\",\"code_hash\":\"0x37f5b3a52c9065715203610d8419abb5f6dd59c4195b5d4c6442407c0c5032a9\",\"code_size\":6929,\"count\":2089},{\"address\":\"0xa5f565650890fba1824ee0f21ebbbf660a179934\",\"code_hash\":\"0x63c8abc3d5aa196fef7b9f43f9a2aeb1217361ccb131bf35f5588d32753a6c17\",\"code_size\":1102,\"count\":2035},{\"address\":\"0x9afe2565af9828c15a7474ddc1c59a1ea7367a5d\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1991},{\"address\":\"0x870ac11d48b15db9a138cf899d20f13f79ba00bc\",\"code_hash\":\"0x73b578a0cd95d0d6e77f85a3945a670a9b8679670f8fc190ca97e89a1f07f6cd\",\"code_size\":2717,\"count\":1944},{\"address\":\"0xcfb26df385d790aa7e417394ec1196a3bd56aa8c\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1909},{\"address\":\"0x3e88c9b0e3be6817973a6e629211e702d12c577f\",\"code_hash\":\"0xb270d1196cc64f337188675d524072547b47e1fc7ec03b163f9f34f820f7ab98\",\"code_size\":21594,\"count\":1835},{\"address\":\"0xa7ca2c8673bcfa5a26d8ceec2887f2cc2b0db22a\",\"code_hash\":\"0x13b2293637be8e1ae1dd8143ed3e02d9b5de340cc8bcf77756415e374233aaf3\",\"code_size\":45,\"count\":1832},{\"address\":\"0x3c3d457f1522d3540ab3325aa5f1864e34cba9d0\",\"code_hash\":\"0x637863b4940357181f66fc748cf3133b516864f6251bf89fde8dcf019d862522\",\"code_size\":13904,\"count\":1763},{\"address\":\"0x98f3c9e6e3face36baad05fe09d375ef1464288b\",\"code_hash\":\"0xadf9db5c37ff049019dd27726900e248d3dc675cee2b4eaf5ecaf18457a36b42\",\"code_size\":680,\"count\":1763},{\"address\":\"0xe0554a476a092703abdb3ef35c80e0d76d32939f\",\"code_hash\":\"0x65154c5c5a7a171a3d89df22d2a11319ddcb60bdf00ff9a4e3bd7312cb6badce\",\"code_size\":22142,\"count\":1753},{\"address\":\"0x6982508145454ce325ddbe47a25d4ec3d2311933\",\"code_hash\":\"0x4bcc3abefd723de3cac53e7c37894ae0cad2f6065e92c8901b0a086ae3386ab6\",\"code_size\":4517,\"count\":1683},{\"address\":\"0x6b175474e89094c44da98b954eedeac495271d0f\",\"code_hash\":\"0x4e36f96ee1667a663dfaac57c4d185a0e369a3a217e0079d49620f34f85d1ac7\",\"code_size\":7904,\"count\":1509},{\"address\":\"0x12dda8bffdbebf79502b175fa1413a4765e69b2f\",\"code_hash\":\"0x3184df2c5119f1ac4098d21644b793d38cafdb2712794f0ae053611021a0f5b2\",\"code_size\":24523,\"count\":1479},{\"address\":\"0x80a64c6d7f12c47b7c66c5b4e20e72bc1fcd5d9e\",\"code_hash\":\"0x0e42165348c9fef8f8381bd60d5276087423604d3f51cabec442610b09b1f5ae\",\"code_size\":797,\"count\":1479},{\"address\":\"0x9c45979137630a06bcede7c75d6a52aa40eff841\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1454},{\"address\":\"0x4f1aac70b303818ddd0823570af3bb46681d9bd8\",\"code_hash\":\"0x0b54b2f6ee698e40ff8796562fe369eb43301c5fa1563dcbdc675f7b929021e5\",\"code_size\":6749,\"count\":1420},{\"address\":\"0xcbff3004a20dbfe2731543aa38599a526e0fd6ee\",\"code_hash\":\"0x4031de700c2e5e41189dffb62c430bcaea2493c7ca522721634c3747647bea97\",\"code_size\":10935,\"count\":1407},{\"address\":\"0xfca88920ca5639ad5e954ea776e73dec54fdc065\",\"code_hash\":\"0x643924e3649cbbad7e2e7ea5ae135318158a28d6b44d2e0c9a2cf7263e1a8bcb\",\"code_size\":19843,\"count\":1403},{\"address\":\"0x3328f7f4a1d1c57c35df56bbf0c9dcafca309c49\",\"code_hash\":\"0x4d9be648c5bf39973670d9f8b481d5d0b971e6a2db2deccc6b98cde21c5dd83e\",\"code_size\":2227,\"count\":1386},{\"address\":\"0x35fc556d6f8675b26fdf1542e6e894100155b34e\",\"code_hash\":\"0x5bfdee31d71cb9b7166d4638c53eded84a657c91fc6d8e25d126526acd1f4c77\",\"code_size\":23828,\"count\":1386},{\"address\":\"0x3c55986cfee455e2533f4d29006634ecf9b7c03f\",\"code_hash\":\"0x6da3cdc5a2d85016fc1cc84ac169ccf7149868813f24d6f10a0add8e9dc431d1\",\"code_size\":1170,\"count\":1375},{\"address\":\"0xc5f2764383f93259fba1d820b894b1de0d47937e\",\"code_hash\":\"0xcbefb55115e50f56c46db0b365637cfade8e3d6fbc2a67ea20be6bc6a137e341\",\"code_size\":20573,\"count\":1375},{\"address\":\"0x6ec94f50cadcc79984463688de42a0ca696ec2db\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1360},{\"address\":\"0x055fb841cce69000fbaff2691ad39fa6e23826a1\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1353},{\"address\":\"0xc7bbec68d12a0d1830360f8ec58fa599ba1b0e9b\",\"code_hash\":\"0x61bb013bc7f8bed8e3c050464e93985787d254f4236d2e3ef846700c07e00aed\",\"code_size\":22142,\"count\":1351},{\"address\":\"0x52aa899454998be5b000ad077a46bbe360f4e497\",\"code_hash\":\"0xb3a458c72a3a0b3aa773f2c3e21d0eadfb06dacdc4568f447d9019d1c1451c35\",\"code_size\":4462,\"count\":1279},{\"address\":\"0x1f9840a85d5af5bf1d1762f925bdaddc4201f984\",\"code_hash\":\"0xdeba17f16fdba566b45d8019575e068625403cc6986fa17ceadd6edf08aa0868\",\"code_size\":12567,\"count\":1240},{\"address\":\"0x80300480c5ec223816b62784e85daff96257a8aa\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1211},{\"address\":\"0x52c7aa73dc430dab948eee73ea253383fd223420\",\"code_hash\":\"0xa3d06f305f17e75aa8f4af2eb21f3ec7e85a71975f13183bef37c1efc1ad02e0\",\"code_size\":9949,\"count\":1194},{\"address\":\"0x7afa9d836d2fccf172b66622625e56404e465dbd\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":1183},{\"address\":\"0x5c7bcd6e7de5423a257d81b442095a1a6ced35c5\",\"code_hash\":\"0x932cddc50793da935ccf915651ad67f6b746e9936fcc5614f0ff492563782c75\",\"code_size\":680,\"count\":1176},{\"address\":\"0x0190a2328e072fc5a7fa00f6c9ae2a16c7f4e32a\",\"code_hash\":\"0x0e757c14f2d75997e4da9f1cfe6ebdbe8d58d95f875870e5399810aa92e2ea94\",\"code_size\":23796,\"count\":1176},{\"address\":\"0x7cfd34ca2dceca6c835adc7e61409a089cfff14a\",\"code_hash\":\"0x1d3faaf2552288f2bb9daac1bd35e2db10279ebfdcb8448e5c33cbbc2b3d4c2f\",\"code_size\":16501,\"count\":1164},{\"address\":\"0xf8ea18ca502de3ffaa9b8ed95a21878ee41a2f4a\",\"code_hash\":\"0x92ca6d03f8eee1d3b7b7430ef06754b46b7a791ced575c3b0ceff5c0179faae4\",\"code_size\":13805,\"count\":1147},{\"address\":\"0x1231deb6f5749ef6ce6943a275a1d3e7486f4eae\",\"code_hash\":\"0x828f8a0694bfba25c80a406283f149d701cdc944acbbc18b57315cf157db9220\",\"code_size\":5176,\"count\":1140},{\"address\":\"0x70cbb871e8f30fc8ce23609e9e0ea87b6b222f58\",\"code_hash\":\"0xb6a0916f3f4f33110bd1c57652c6e21f4beb32ffcfb50124f8b51cb5cee61f04\",\"code_size\":1941,\"count\":1124},{\"address\":\"0x40aa958dd87fc8305b97f2ba922cddca374bcd7f\",\"code_hash\":\"0xe8711c5f0fe7f3c28078140bb97b65aa015a58c06c14bad5abffa44f00f1ddf5\",\"code_size\":2133,\"count\":1124},{\"address\":\"0x5703b683c7f928b721ca95da988d73a3299d4757\",\"code_hash\":\"0x4ed4e95f36999ef014fbd01a2d4a5ec8e80ac59b72c9440a1bfba11198a41413\",\"code_size\":2180,\"count\":1098},{\"address\":\"0x7effd7b47bfd17e52fb7559d3f924201b9dbff3d\",\"code_hash\":\"0x71967695f3c266ab58af4b2bd527d789c6f26ff08309b218f45a28de003eab81\",\"code_size\":13783,\"count\":1082},{\"address\":\"0x74de5d4fcbf63e00296fd95d33236b9794016631\",\"code_hash\":\"0x34285df013b925b34c7744f619f6ed193cbb11e1e08b0013af1dd0511a3e6f6e\",\"code_size\":1163,\"count\":1058},{\"address\":\"0x1f2f10d1c40777ae1da742455c65828ff36df387\",\"code_hash\":\"0x14a86b6d239e89ae47d307fa4a7fb6843e4356f5d13ac751a053711a88b36658\",\"code_size\":13835,\"count\":1050},{\"address\":\"0xb4e16d0168e52d35cacd2c6185b44281ec28c9dc\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":1043},{\"address\":\"0x55877bd7f2ee37bde55ca4b271a3631f3a7ef121\",\"code_hash\":\"0x86ce3ac4e3fc298615c5371eef48b5b1af84ecf6aeef8fcad7023f0a3cccdbff\",\"code_size\":24378,\"count\":975},{\"address\":\"0xa69babef1ca67a37ffaf7a485dfff3382056e78c\",\"code_hash\":\"0xfe6b10a47e6f02df629ebc024bfceb97211d1343a434a71e8421cfe73180ed18\",\"code_size\":11793,\"count\":968},{\"address\":\"0x3a10dc1a145da500d5fba38b9ec49c8ff11a981f\",\"code_hash\":\"0xd7010b2f8fcff1164064185f94083f3c74de8227c5562969b73d88a257b9d13b\",\"code_size\":2115,\"count\":962},{\"address\":\"0x836bb8af49cc2d0ca92f92d4d0c6fbffe52575ec\",\"code_hash\":\"0x7fce9930b8932647aedda55051d27d13eb15802c07825d8ed1d092680654a15f\",\"code_size\":22570,\"count\":962},{\"address\":\"0x9995855c00494d039ab6792f18e368e530dff931\",\"code_hash\":\"0xde5deeacc3e89f91cbbf91f90f4ec67c60c4865a2d05e80fce52f431d6707556\",\"code_size\":11342,\"count\":958},{\"address\":\"0xe592427a0aece92de3edee1f18e0157c05861564\",\"code_hash\":\"0xbb90113d2f9a5e9b7feb15a1d1fff06c1ee1575b3f9b1181778ffd0cf633e7ea\",\"code_size\":12070,\"count\":956},{\"address\":\"0xee56f191001f1ef885f67e86413f86a39976c20b\",\"code_hash\":\"0xb87dbdbc5343bc98d2267fab18eeeae5a80e102c1c4caeb34ab4fabdc0fafad1\",\"code_size\":15395,\"count\":948},{\"address\":\"0x87870bca3f3fd6335c3f4ce8392d69350b4fa4e2\",\"code_hash\":\"0x96107dc4006b4c7fecd1827cfb275ffeef31e6194cd50466f85f8eb24ccf2679\",\"code_size\":2400,\"count\":933},{\"address\":\"0x9aeb8aaa1ca38634aa8c0c8933e7fb4d61091327\",\"code_hash\":\"0xa87c123fc6ccf5e52ec2301ad5a0b9edb98b498f51b90e0ca6c0f321ebc3a06d\",\"code_size\":21457,\"count\":933},{\"address\":\"0x68fe80c6e97e0c8613e2fed344358c6635ba5366\",\"code_hash\":\"0x5058753070715b5db9db2ddf506080412e9e8de778ef459050f9c3db60b9303e\",\"code_size\":13743,\"count\":882},{\"address\":\"0x00c452affee3a17d9cecc1bcd2b8d5c7635c4cb9\",\"code_hash\":\"0xb21b7d6fbab67601893f6996e761a4c9c358ae87a8fbf20d22cd251293fff848\",\"code_size\":845,\"count\":882},{\"address\":\"0x514910771af9ca656af840dff83e8264ecf986ca\",\"code_hash\":\"0x77c633ba07c8cb94cd4864092fd8b31e31cf9d065f6fb6acf617298bc0008785\",\"code_size\":3153,\"count\":877},{\"address\":\"0xe9ee6923d41cf5f964f11065436bd90d4577b5e4\",\"code_hash\":\"0x942ab4e448f8077fc93174b4839b422295a8d6e4b406780fd678030ff0f21fd2\",\"code_size\":24488,\"count\":871},{\"address\":\"0x8b71140ad2e5d1e7018d2a7f8a288bd3cd38916f\",\"code_hash\":\"0xb21b7d6fbab67601893f6996e761a4c9c358ae87a8fbf20d22cd251293fff848\",\"code_size\":845,\"count\":871},{\"address\":\"0xaaaaaaae92cc1ceef79a038017889fdd26d23d4d\",\"code_hash\":\"0x9e4bd525dd3b86feec46ae5c92119318696427ad0fc5c57667ae7d02efd08f9b\",\"code_size\":2726,\"count\":871},{\"address\":\"0x9bc8640f0b0ec9020495957bd9fc0a1d1045772d\",\"code_hash\":\"0x6362664c333f91766a2563f02aa4ff4944bfb9b31747d45b672929e477336756\",\"code_size\":6824,\"count\":855},{\"address\":\"0x7fc66500c84a76ad7e9c93437bfc5ac33e2ddae9\",\"code_hash\":\"0x98a3150c9759d754a4b31f6fb3f4828371c60c2c07fb0754cfa2b230cb601001\",\"code_size\":2491,\"count\":843},{\"address\":\"0x5d4aa78b08bc7c530e21bf7447988b1be7991322\",\"code_hash\":\"0x8f519b5e323e237080b6ef72c52a3e93ebeeff687bf44c6817db4a8b3d0d2ff2\",\"code_size\":8655,\"count\":843},{\"address\":\"0xfbd4cdb413e45a52e2c8312f670e9ce67e794c37\",\"code_hash\":\"0x0b9e30ca178fb7c5b77ee653b6e15ab158d4dd236a1f503de3fb02b02b8d1e21\",\"code_size\":15871,\"count\":822},{\"address\":\"0x6c3ea9036406852006290770bedfcaba0e23a0e8\",\"code_hash\":\"0xbc22d0b1173d9ff26383e64a50a807afa931a2809a7b6bae3b051723a1a9ebe1\",\"code_size\":1506,\"count\":821},{\"address\":\"0x7302ea4e51b041b691d1f3458fa7d36560f90708\",\"code_hash\":\"0x896916e0ba652415b534dbc9a321d3a151545ed198a63315d0613f28ae4bafd1\",\"code_size\":15420,\"count\":821},{\"address\":\"0x51c72848c68a965f66fa7a88855f9f7784502a7f\",\"code_hash\":\"0x2643a93e20c87973a77d1e8912dab01918639d02a816331a4206f77a8ad9e4c5\",\"code_size\":20805,\"count\":783},{\"address\":\"0x0de8bf93da2f7eecb3d9169422413a9bef4ef628\",\"code_hash\":\"0x009758e5506fc34973129ce0a755dded5d10dce1aff95952ff9e28ad28a66fa4\",\"code_size\":3821,\"count\":783},{\"address\":\"0x2b33cf282f867a7ff693a66e11b0fcc5552e4425\",\"code_hash\":\"0xfb68276e556a938a40d9b1d771def5e66f94e466e4bc12b5fea458dfeb0daccd\",\"code_size\":12028,\"count\":775},{\"address\":\"0xb8ffc3cd6e7cf5a098a1c92f48009765b24088dc\",\"code_hash\":\"0x3ac64c95eedf82e5d821696a12daac0e1b22c8ee18a9fd688b00cfaf14550aad\",\"code_size\":898,\"count\":772},{\"address\":\"0xb8713bd23c33b15236af67e5badceb8e49cb0244\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":770},{\"address\":\"0x7d4e742018fb52e48b08be73d041c18b21de6fb5\",\"code_hash\":\"0x16f41184f797cb8f8918680df0ebf2a97cc3192aa6b104615f61096fc674f2aa\",\"code_size\":22337,\"count\":762},{\"address\":\"0xe0f63a424a4439cbe457d80e4f4b51ad25b2c56c\",\"code_hash\":\"0x609392ad0852ef7ee3eb446922aa7d64da8d1524302b7c98b06029e28c5d309e\",\"code_size\":13914,\"count\":759},{\"address\":\"0xd9db270c1b5e3bd161e8c8503c55ceabee709552\",\"code_hash\":\"0xbba688fbdb21ad2bb58bc320638b43d94e7d100f6f3ebaab0a4e4de6304b1c2e\",\"code_size\":22958,\"count\":753},{\"address\":\"0x3157874a7508fcf972379d24590c6806522b784f\",\"code_hash\":\"0x2c28fce076df65df992c0bfc6cd9b42bfff5645b1f845b87fedaa9d324ee9dc6\",\"code_size\":7275,\"count\":746},{\"address\":\"0x69af81e73a73b40adf4f3d4223cd9b1ece623074\",\"code_hash\":\"0x0fb895160b3fb547467539897465eb257e378ae5f1e959ad7bdc3ab45ed2b1cc\",\"code_size\":4331,\"count\":734},{\"address\":\"0x6e4141d33021b52c91c28608403db4a0ffb50ec6\",\"code_hash\":\"0x2596d70ee00d95e6b8bddee635caeaf4acb4cc321929c86d0b850d6d343ac1cb\",\"code_size\":14248,\"count\":733},{\"address\":\"0xc36442b4a4522e871399cd717abdd847ab11fe88\",\"code_hash\":\"0x692e658b31cbe3407682854806658d315d61a58c7e4933a2f91d383dc00736c6\",\"code_size\":24384,\"count\":731},{\"address\":\"0x666acd390fa42d5bf86e9c42dc2fa6f6b4b2d8ab\",\"code_hash\":\"0x59953cb01c1d035fdfcc6610ce14ac9b5e2a81774257ed11d4de76783e93b071\",\"code_size\":8517,\"count\":725},{\"address\":\"0xc02ab410f0734efa3f14628780e6e695156024c2\",\"code_hash\":\"0x013ab0590a1572bec93a59a784f56ba238c6a8da11a228704ba0d081d18a9d35\",\"code_size\":11010,\"count\":712},{\"address\":\"0x5f4ec3df9cbd43714fe2740f5e3616155c5b8419\",\"code_hash\":\"0x4b79b5c8aee6da0f7b393e8b53e6265ef7320a1d16184c65bd3841b5aa3d700d\",\"code_size\":9571,\"count\":704},{\"address\":\"0x35465d7b8ec8f28b06c90ab562c85a012337f687\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":701},{\"address\":\"0xf78e87f183c15a5f526d796c1bea9e9c3643b327\",\"code_hash\":\"0xecd2f93c9cadd702b1e5d4a15f46c38d04b93f7d28d7360148f84cfa7ee06f10\",\"code_size\":7485,\"count\":688},{\"address\":\"0x17144556fd3424edc8fc8a4c940b2d04936d17eb\",\"code_hash\":\"0x9da2a3dadf3a39f99886a2958f2c239bf663d39b766e2eb13e37d9c73500a9d0\",\"code_size\":23361,\"count\":679},{\"address\":\"0xae7ab96520de3a18e5e111b5eaab095312d7fe84\",\"code_hash\":\"0xb9c1c929064cd21734c102a698e68bf617feefcfa5a9f62407c45401546736bf\",\"code_size\":1035,\"count\":679},{\"address\":\"0x2a9db31f0f0329b03ddd7a8a4b5297815bba0124\",\"code_hash\":\"0x66ac72fdaa86289a353133482a0ccac1ec74f143c3e0f029e13e3b24b9ed7f53\",\"code_size\":13861,\"count\":678},{\"address\":\"0x1a44076050125825900e736c501f859c50fe728c\",\"code_hash\":\"0xb747fab405fadff7fc9d8adb083d18d3454ac58ffdefe9121ed5f008f57d93e0\",\"code_size\":24005,\"count\":677},{\"address\":\"0x881d40237659c251811cec9c364ef91dc08d300c\",\"code_hash\":\"0xcc34a85a74e46f422c2b06b16156799b7c313a71390b4465cbc463bd99d76764\",\"code_size\":8320,\"count\":676},{\"address\":\"0x90001c5f2c6ffd4b90a801afddefcbf31965c667\",\"code_hash\":\"0xbe03edebdddb7fe90027946f0ec2a2c154d787594613c635f0616678b2d2d6c5\",\"code_size\":13795,\"count\":673},{\"address\":\"0x6039d41712fcedc37e63e0d9631075721f5c5c86\",\"code_hash\":\"0x32b9a0c6a43a6426dd1025fb1e97d198360dca8174d78c96306a0f19ec853aaf\",\"code_size\":163,\"count\":662},{\"address\":\"0xac725cb59d16c81061bdea61041a8a5e73da9ec6\",\"code_hash\":\"0x1c6bfa34fcf568d72d11492bf3821c47f63c11a2c13457986ae97e1ac03c6b8b\",\"code_size\":9403,\"count\":662},{\"address\":\"0x20d7c3db4b3149e18dc88fb5b5ae58b6d58d22c2\",\"code_hash\":\"0xa8578c00535b7611eb6565c620340ceb0586972b27f11c12382d133c75b0b576\",\"code_size\":8658,\"count\":662},{\"address\":\"0x404d3295c8b1c61662068db584125a7ebcc0d651\",\"code_hash\":\"0x381f34c1a3ec614afb4e79e98b4418d63f7307dfb7653f92c5e792fbfc52266b\",\"code_size\":12526,\"count\":657},{\"address\":\"0xf816507e690f5aa4e29d164885eb5fa7a5627860\",\"code_hash\":\"0xb8b3b0faf67b41d2b2cf07d95961f378fbcf1dcdf4ef376db2d1ef3a4ececff2\",\"code_size\":14449,\"count\":654},{\"address\":\"0xaa59a1d797c417d79f4509cb83e09428c4b5ffd9\",\"code_hash\":\"0x076cc532f9b55c671ccc8213de73f50ce7e36a430dc0f762f664388abda6227c\",\"code_size\":5163,\"count\":650},{\"address\":\"0xfde73eba30891d103e3c7402f6aca9860992357c\",\"code_hash\":\"0xda59043475b50e5e40717a0229975cc0d93d6df788715393284f167ceeac68dd\",\"code_size\":8673,\"count\":650},{\"address\":\"0x88909d489678dd17aa6d9609f89b0419bf78fd9a\",\"code_hash\":\"0x75722002fa428f88b74441bb6a22ff059625a6a21430386d46bba29677590399\",\"code_size\":183,\"count\":640},{\"address\":\"0xef3c9aa3928adccd11103ec1a190b63f34807c34\",\"code_hash\":\"0xf0eab41832d93cc0f6641932e2868c0cd2d9482f27d6bd0af3fee944d1a14855\",\"code_size\":10371,\"count\":640},{\"address\":\"0x4c9edd5852cd905f086c759e8383e09bff1e68b3\",\"code_hash\":\"0x6ca5462ff0355c610301f701d0bb1136becca144e195da86d240b5e604ef9625\",\"code_size\":7567,\"count\":631},{\"address\":\"0x824b8fd8b18175c36a4fdc13726aa955f73f04cf\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":630},{\"address\":\"0x046f31ecba77fe9feba89c0a7c61a5a731b825f3\",\"code_hash\":\"0xc1d1980ea0b3c37372d9e1c02fa30d6d428e633bd36b7b7347e3303b9dea284b\",\"code_size\":6743,\"count\":629},{\"address\":\"0xd533a949740bb3306d119cc777fa900ba034cd52\",\"code_hash\":\"0xb76a9f27b7c56b437bd85288ec0b47df9b0dcbc0e8893940e7e1cb7fef862eef\",\"code_size\":4369,\"count\":621},{\"address\":\"0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640\",\"code_hash\":\"0xa981b66c747a3d9fa29d7e200d5faaa2826960523d0e5a0df8148e8868c480b4\",\"code_size\":22142,\"count\":617},{\"address\":\"0xc03f31fd86a9077785b7bcf6598ce3598fa91113\",\"code_hash\":\"0xad7e55b5da5433fcc117aac1b8add852266502bf528ae14d82544d8310257bf5\",\"code_size\":2304,\"count\":616},{\"address\":\"0x319ae539b5ba554b09a46791cdb88b10e4d8f627\",\"code_hash\":\"0x86377689eddea450f0ad8537de94a33d9236c75b3682fb0e6e3d64675ef3561e\",\"code_size\":8437,\"count\":616},{\"address\":\"0xc7953f23fdceb5d836bd4e5ffb478766d256e410\",\"code_hash\":\"0x89d6669e9f1546ae96dfd4d1407489a90bfa6c88237471de9d11aa7fd20a6085\",\"code_size\":14089,\"count\":614},{\"address\":\"0x5dac0e3e4bc880fab204c243b62cfd828c1f79e2\",\"code_hash\":\"0xf1ceb51ad406fe39d2ec9b8dc3df2883376479dea33fe372fa0204156ae61610\",\"code_size\":6685,\"count\":605},{\"address\":\"0x1bc8f124e7e320c71a6394de0458e8d7ea27623e\",\"code_hash\":\"0x66275ee11c3e15aba3c3bdc10b71e4f361dcaf6e9df53233c4e39e11c2d50f68\",\"code_size\":1923,\"count\":600},{\"address\":\"0x4d5db07eee4d4308f21b8b24b7d22e1c404c5e42\",\"code_hash\":\"0xb6190d330bffefb72af0431e4fe2d7ec386e462176c2dc0d1779a21cc2dc7c83\",\"code_size\":14065,\"count\":594},{\"address\":\"0x67712cd5ba8b28c1d0d9bb2842b37e1cd8e3818b\",\"code_hash\":\"0x96a26624f051545d98abdccdb8a6c2a167e81453ab28054d620d0b414eaea8b6\",\"code_size\":14728,\"count\":588},{\"address\":\"0x9cd8f4bc352955a20ff8ed902c58d2ef03ccb578\",\"code_hash\":\"0x9a893d4a0d01522c414b091758fc1ae91ff98c70dead138db3a45b2efb2068e6\",\"code_size\":14506,\"count\":588},{\"address\":\"0x3fc91a3afd70395cd496c647d5a6cc9d4b2b7fad\",\"code_hash\":\"0xc4f0904cd0f741bb3ab2a16013d23b4d72eec59e3cb24879f0f0ba0c3fea24d9\",\"code_size\":17958,\"count\":584},{\"address\":\"0x7f39c581f595b53c5cb19bd0b3f8da6c935e2ca0\",\"code_hash\":\"0x630181884786c962140041cffa3acffbf39d8c93bde1a62b7b55046e57400557\",\"code_size\":6492,\"count\":582},{\"address\":\"0x95222290dd7278aa3ddd389cc1e1d165cc4bafe5\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":580},{\"address\":\"0x6f959fac00556e97e049863f5117b0aaf7c87585\",\"code_hash\":\"0x6b71c780bae84a1c7fc364c6672f8b14eb235e6c348b7b0a6ea9ac21f3746a06\",\"code_size\":5192,\"count\":578},{\"address\":\"0x67c4ca4b83dedfe9ca3ca3eaf6d6f01b2d2bcc7f\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":569},{\"address\":\"0x5d6d71178861867cf68ba942bf6a87b32dbcc9c1\",\"code_hash\":\"0x56974d2643392d940e2d2ddd6b9256e1f7b176a192795e8750685f351b6debc8\",\"code_size\":8280,\"count\":569},{\"address\":\"0xc305d102d03a2567e7a457ff89f273cf227c6a9c\",\"code_hash\":\"0x0630445463a8bc0b793fedd8823a93b29b510b9936e39e71dbb58cae4c155c11\",\"code_size\":12958,\"count\":560},{\"address\":\"0xbbbb2d4d765c1e455e4896a64ba3883e914abbbb\",\"code_hash\":\"0xd6c15581052e39f83af71d1e8c7bb2881506748de70c2f7b59d48b17c7929ce3\",\"code_size\":20473,\"count\":556},{\"address\":\"0x68b36248477277865c64dfc78884ef80577078f3\",\"code_hash\":\"0xd996e051289bfd27cab2dc1870c840a3d844e2062ca084ae1876ba92dc6fc352\",\"code_size\":7500,\"count\":554},{\"address\":\"0x1cd7c7061de2c6546d61cf46e26c548d6e2bd7e5\",\"code_hash\":\"0x26b489514a7e5db50a6e64b889065e2f3d1008457e6c4b865fe9a1d8349a3ebd\",\"code_size\":1009,\"count\":540},{\"address\":\"0xbbbba1ee822c9b8fc134dea6adfc26603a9cbbbb\",\"code_hash\":\"0x11477594f2daa08c3176e01e11a7cbe1e714177f954caf9e234fe0dbffd42952\",\"code_size\":10175,\"count\":540},{\"address\":\"0xf93191d350117723dbeda5484a3b0996d285cecf\",\"code_hash\":\"0x27b5b1a59ec23fcebae66a50f28234ace4671bb222772083de520dfef431d4db\",\"code_size\":2550,\"count\":536},{\"address\":\"0xf93191d350117723dbeda5484a3b0996d285cecf\",\"code_hash\":\"0x27b5b1a59ec23fcebae66a50f28234ace4671bb222772083de520dfef431d4db\",\"code_size\":2550,\"count\":536},{\"address\":\"0x808507121b80c02388fad14726482e061b8da827\",\"code_hash\":\"0x92f367f6228e87a3054e31dd663894bc8e03a3ecf095f374d22f5446a7affe26\",\"code_size\":9943,\"count\":533},{\"address\":\"0xa26148ae51fa8e787df319c04137602cc018b521\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":524},{\"address\":\"0xdc035d45d973e3ec169d2276ddab16f1e407384f\",\"code_hash\":\"0x13670c37dbba93ae2e7a2538198c3048a60f5fbd567e29ba8b45eed10e3e29f5\",\"code_size\":170,\"count\":524},{\"address\":\"0x1923dfee706a8e78157416c29cbccfde7cdf4102\",\"code_hash\":\"0x9dc261be5cba7af9a584b462cf5af78f1b215ac6b6b8e68a2a9058b7419ea100\",\"code_size\":6731,\"count\":524},{\"address\":\"0x83584f83f26af4edda9cbe8c730bc87c364b28fe\",\"code_hash\":\"0x2769e338c9d60a078ef05dd9e03f5fa3e1df133f7da4494c5bcc83a71a9a9892\",\"code_size\":11035,\"count\":520},{\"address\":\"0x5c69bee701ef814a2b6a3edd4b1652cb9cc5aa6f\",\"code_hash\":\"0xbab145d02e7005f0d84c6c1639d39b799b0ea16df99ebbdaf5a14d9da820b4e0\",\"code_size\":13859,\"count\":518},{\"address\":\"0x68b3465833fb72a70ecdf485e0e4c7bd8665fc45\",\"code_hash\":\"0x6ec798e80f3a19de650826338677604e54d6664f44a33b53a20b22b1939f402e\",\"code_size\":24497,\"count\":517},{\"address\":\"0x54586be62e3c3580375ae3723c145253060ca0c2\",\"code_hash\":\"0x45cd52c8cff7bafe9b7003f246cc7df89dfc668d8e95d0414178ef6f9f4a3701\",\"code_size\":3405,\"count\":512},{\"address\":\"0x2e2e7a1f05946ecb2b43b99e3fc2984fa7d7e3bc\",\"code_hash\":\"0x5407e76d4346711dbdedaea9caffc356841c1fbd8147ef6f3709f3491291c615\",\"code_size\":7042,\"count\":511},{\"address\":\"0x4838b106fce9647bdf1e7877bf73ce8b0bad5f97\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":511},{\"address\":\"0xb300000b72deaeb607a12d5f54773d1c19c7028d\",\"code_hash\":\"0xa9e9d5c129cfbdd7924803fdc1b3f66f5ba3067f3903a59b44db23f3a1f13677\",\"code_size\":180,\"count\":509},{\"address\":\"0xe18bc148b554d32371c63d3aa7659e7ab2d40b44\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":500},{\"address\":\"0x1d6103243d0507a9d1314bac09379bf57a5cf155\",\"code_hash\":\"0xed6fff9ac3899bd7efcbd26e254ce1cc182af72c8f36f4b1065d6e4112ac1441\",\"code_size\":11170,\"count\":500},{\"address\":\"0xa4c04e7598c5147113b7b03f606b524c630143ce\",\"code_hash\":\"0xe9a4a94aab50f988e817762a200d00605b2de1091c21dc507f24a3fac575141c\",\"code_size\":24419,\"count\":500},{\"address\":\"0xe9010521dfbc4c9050f30c92b20a1579367a0214\",\"code_hash\":\"0xaaebe85cf8a2990b78bcfcca306b50f7388ffb246782f7d72df8a6bce47d8961\",\"code_size\":6776,\"count\":500},{\"address\":\"0x0d4a11d5eeaac28ec3f61d100daf4d40471f1852\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":494},{\"address\":\"0xa906421af629452a44553b62cc2f1e1e3426bd97\",\"code_hash\":\"0x3f49d39027083f9ab87248d1cc2398a8a4a5ace558998fcb2cb03e4c49c523fd\",\"code_size\":1283,\"count\":492},{\"address\":\"0x149bb80f4069a11124df0492f4caf10c986ac5fd\",\"code_hash\":\"0x49246703309490b77fe02adc18876837093ec27c1645cde57e927c319de51984\",\"code_size\":6903,\"count\":489},{\"address\":\"0x0000000000000068f116a894984e2db1123eb395\",\"code_hash\":\"0x74499ac0cce14428e4b41541d5e44f28f5a6882a1051d0118867c2a93cd5aec0\",\"code_size\":23981,\"count\":488},{\"address\":\"0x8164cc65827dcfe994ab23944cbc90e0aa80bfcb\",\"code_hash\":\"0x96107dc4006b4c7fecd1827cfb275ffeef31e6194cd50466f85f8eb24ccf2679\",\"code_size\":2400,\"count\":487},{\"address\":\"0xe7b67f44ea304dd7f6d215b13686637ff64cd2b2\",\"code_hash\":\"0x8a4eae7c114abcb537a4a008f53562db56dccad54928b342fc97a073b805874a\",\"code_size\":18645,\"count\":487},{\"address\":\"0x000056f7000000ece9003ca63978907a00ffd100\",\"code_hash\":\"0x9e6930be6759e905574791278e46db4d510c072733fe62a0c7d4e6e76f13e324\",\"code_size\":6967,\"count\":482},{\"address\":\"0x3071c580c95882691be2179c41849ed9be61e1c3\",\"code_hash\":\"0xfa7288e02f4c34d263c1359d435187087f407402521b6362b317eaa44b3db758\",\"code_size\":13254,\"count\":480},{\"address\":\"0xfaba6f8e4a5e8ab82f62fe7c39859fa577269be3\",\"code_hash\":\"0x7643c7b2b6fb528148258f2df24a4886a16ef4ef0c24a66d450b30c3745e037e\",\"code_size\":11822,\"count\":476},{\"address\":\"0xec53bf9167f50cdeb3ae105f56099aaab9061f83\",\"code_hash\":\"0x3120aec785e7d6f8e12eed8fc386018db9b6acd0db21a87845c88a935798ca9e\",\"code_size\":2188,\"count\":475},{\"address\":\"0x17f56e911c279bad67edc08acbc9cf3dc4ef26a0\",\"code_hash\":\"0x056499ee980555cf89b64b41a912ac2481ce3a8cd78dae0ccd8513146780699a\",\"code_size\":13478,\"count\":475},{\"address\":\"0x3ee18b2214aff97000d974cf647e7c347e8fa585\",\"code_hash\":\"0x26aeb96c4320b04eeda9660276adf5920d056be2e1bee7f038255950e60d0a27\",\"code_size\":680,\"count\":470},{\"address\":\"0x381752f5458282d317d12c30d2bd4d6e1fd8841e\",\"code_hash\":\"0x7b5dbbb9333e8973eb3e6ed329cf1505c087d69c4d7bfd3cf76a48a09c445ffc\",\"code_size\":23716,\"count\":470},{\"address\":\"0xc38e4e6a15593f908255214653d3d947ca1c2338\",\"code_hash\":\"0x3454a2dbdd2df261c9dc536ae8d717c2482cbd06aa14f4888cd8f7343cc24c52\",\"code_size\":23669,\"count\":469},{\"address\":\"0x382ffce2287252f930e1c8dc9328dac5bf282ba1\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":465},{\"address\":\"0x95ad61b0a150d79219dcf64e1e6cc01f0b64c4ce\",\"code_hash\":\"0xd0caa0f9bc744c523933d44e6d8d07f868803d10bf16c8129e12f670296175ad\",\"code_size\":4852,\"count\":464},{\"address\":\"0x4e9623b7e5b6438542458f5ee828d65c24d3af8c\",\"code_hash\":\"0xc4f8273e02ed47848342adfe264a7e211862397bdd81be9435151de4d2cbdd79\",\"code_size\":9165,\"count\":460},{\"address\":\"0x2a56407882e274275f1d66dd2098b7d1ce6fd71f\",\"code_hash\":\"0x57cb3769f97463146ad48077a8ae35961c65c6ff2b3a606a9819758bd631d7f9\",\"code_size\":1994,\"count\":457},{\"address\":\"0x53b1030e68f2aecfad04794458ace54ec06dc707\",\"code_hash\":\"0x214db521721cb1062143c38a2f9513a329b125260e0b35575b0816e595d4cca9\",\"code_size\":2184,\"count\":457},{\"address\":\"0xaaee1a9723aadb7afa2810263653a34ba2c21c7a\",\"code_hash\":\"0xf35998db8fc462e3d1f3029ca4271000d552bb98bbfe486bc5e50545ea327bdf\",\"code_size\":8321,\"count\":453},{\"address\":\"0x9392a42abe7e8131e0956de4f8a0413f2a0e52bf\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":447},{\"address\":\"0xe7351fd770a37282b91d153ee690b63579d6dd7f\",\"code_hash\":\"0x6bec2bf64f7e824109f6ed55f77dd7665801d6195e461666ad6a5342a9f6daf5\",\"code_size\":2112,\"count\":440},{\"address\":\"0x33b72f60f2ceb7bdb64873ac10015a35bed81717\",\"code_hash\":\"0x1fb0841b24573c1a9f4091471d49131c79d2aae0d127c265836124dcc0a3db1f\",\"code_size\":24030,\"count\":440},{\"address\":\"0x6dc18a14c7cfa54cc34ee6a776adc0d0d6f3bccf\",\"code_hash\":\"0xcd617e3564f68c81287d2ebc5291e686b1317786de456f56a4180705f25b5f1c\",\"code_size\":3602,\"count\":435},{\"address\":\"0x9ac9468e7e3e1d194080827226b45d0b892c77fd\",\"code_hash\":\"0xd0b551b7fb6095fca704eee524e7a689394199e358dd891c8c50a4f8480980e8\",\"code_size\":7279,\"count\":434},{\"address\":\"0xc2d1ca8b6147b606bf0668a5c8ab8c15d04426b6\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":434},{\"address\":\"0xdadb0d80178819f2319190d340ce9a924f783711\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":431},{\"address\":\"0x66e835e70ef4283a2ff6070743e061e6debba921\",\"code_hash\":\"0x120fe4a817bd8434f73dffc2cd80e42be7d8b731ac04a8f763987aa84c72af1a\",\"code_size\":14019,\"count\":426},{\"address\":\"0xb685760ebd368a891f27ae547391f4e2a289895b\",\"code_hash\":\"0x7866a7d1a43118298bc65ebbc45ecfc0423df8ffab4c9e49decf80dd0f6867a4\",\"code_size\":4503,\"count\":425},{\"address\":\"0xf42845b7fd65709f251146ab373933f20e9d7c41\",\"code_hash\":\"0xc0d4f6eba689fb83d08375a62736c7c0f8665745fc2aa72c72add536ac26296d\",\"code_size\":14475,\"count\":423},{\"address\":\"0x0ce9dde7927d60b0f2e686e8937c2d44df912b64\",\"code_hash\":\"0xc2ac031454c25ad2d5ce6a435d95574e6aefc490093bf6814740ee42bce5d684\",\"code_size\":6145,\"count\":422},{\"address\":\"0xbd6c7b0d2f68c2b7805d88388319cfb6ecb50ea9\",\"code_hash\":\"0x54a2621603b7e3d32bb26dc7fecdd6e90856e03cc43e7deecee9ef3e59adb019\",\"code_size\":5760,\"count\":420},{\"address\":\"0xce9a49673fd1d40d38f6061eda5437dc84bd75f0\",\"code_hash\":\"0x0d4544f264ebc832ef8c21ded4db38b879a807ec6795fbd3f220854c736add0f\",\"code_size\":2428,\"count\":419},{\"address\":\"0xc7ba94123464105a42f0f6c4093f0b16a5ce5c98\",\"code_hash\":\"0x824ea4d29d46b1519a177b7a2285141fedc843fea751b047d7c3bba447919591\",\"code_size\":1130,\"count\":418},{\"address\":\"0x1820a4b7618bde71dce8cdc73aab6c95905fad24\",\"code_hash\":\"0xf0aa940bb32e37c5f7268b53acc48c7cdd148cd0fc196f30faa00a4d66c0443a\",\"code_size\":2501,\"count\":415},{\"address\":\"0x9b9d5035f0dc2865c8e27de2d1556cd3c726d871\",\"code_hash\":\"0x95af6ad61b8d715878fd88e04e0feb6e62d100947bb6c2c8bd53dcbe443647fe\",\"code_size\":1320,\"count\":413},{\"address\":\"0x2cb8670ca0cace687a106c4c3a2d36cd4f6d0305\",\"code_hash\":\"0x43b44e31d19805555d6ab2116c619249f1b3b689d35e2631db6bcfc03763f788\",\"code_size\":6158,\"count\":412},{\"address\":\"0xb2f38107a18f8599331677c14374fd3a952fb2c8\",\"code_hash\":\"0x4765157b6367be79221944411883dbc6c3e31e92fddfb04024ce6d7ae9d8da8b\",\"code_size\":281,\"count\":410},{\"address\":\"0xd00e5cada9de91cba0316776078cc3ab5b8f2b85\",\"code_hash\":\"0x07565f0502d6bf39e1ced63394929e7c00ade16165ad0779b8716ebea0a6d6cb\",\"code_size\":6873,\"count\":408},{\"address\":\"0x69787f3d1375e9e092eb5a4f3106abc24494a573\",\"code_hash\":\"0x8b13e730b3694a7186fbdb58738a827f676a3b052eeb239b25c4de32d3cde6bf\",\"code_size\":17586,\"count\":404},{\"address\":\"0x508defdb5dd2adeefe36f58fdcd75d6efa36697b\",\"code_hash\":\"0x09fa28928d63e04c6aac39b8505e34bc4900773c6bcca9f97b66ea5e18885ab9\",\"code_size\":2919,\"count\":404},{\"address\":\"0x8db49233e3b7691d68745a31e4a0cd9cf924b7e9\",\"code_hash\":\"0xe62a7750873679df7c60f618573549a328d7007c075696d562bacdd53ad7f773\",\"code_size\":11191,\"count\":402},{\"address\":\"0x91e677b07f7af907ec9a428aafa9fc14a0d3a338\",\"code_hash\":\"0x073c6ec3b11b1c79a5e70af69992743e1f9857ca449d45cf954c164b0e464a9a\",\"code_size\":2151,\"count\":402},{\"address\":\"0xfe9ab78ed4f9f3dbb168d9f5e5213d78605c9805\",\"code_hash\":\"0xe377dd7a262f009806b0a6cbd290d6f9c94903929bbcd158c04b1e7578e5b466\",\"code_size\":19571,\"count\":398},{\"address\":\"0x173272739bd7aa6e4e214714048a9fe699453059\",\"code_hash\":\"0x80728b513ccd6a951aa35386a149a52ec8c76c9fe1ccd7955461eff2a220827a\",\"code_size\":2304,\"count\":398},{\"address\":\"0xa250cc729bb3323e7933022a67b52200fe354767\",\"code_hash\":\"0xbe63f68c8de6e3f7cf411d9b117b931128468c36e0753fc5878fd5359f16acf5\",\"code_size\":6977,\"count\":398},{\"address\":\"0x74d8b81e1ce4d66ec0c63ee212b1cb57180b2da9\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":397},{\"address\":\"0x1e0049783f008a0085193e00003d00cd54003c71\",\"code_hash\":\"0x069efdc9b946a332dce9951324fa197268e3ff0e00e44c6bf36049fc53113a41\",\"code_size\":3190,\"count\":397},{\"address\":\"0x9726746c6b96b489715185071b3be774521bc5b7\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":396},{\"address\":\"0xef4c4bcbe105170810b6ef58a286d9ce97a1fabe\",\"code_hash\":\"0x5dc594514ae44162526b44aaa1312f50cea9e36eccfcfd467de12c0efc734e4f\",\"code_size\":12948,\"count\":396},{\"address\":\"0x9008d19f58aabd9ed0d60971565aa8510560ab41\",\"code_hash\":\"0x744d58584e38d214eb190629f131d5cf8b8703bd68e04452f9692177c37c4bc9\",\"code_size\":16165,\"count\":394},{\"address\":\"0xd62a650f4306d2d709d8580c8a9efd419d65ed80\",\"code_hash\":\"0xd3f6d63295d3fc313012bb1eaa3a96a289bd330c76293371ccbd59f62c7ae47d\",\"code_size\":14019,\"count\":394},{\"address\":\"0xa93d86af16fe83f064e3c0e2f3d129f7b7b002b0\",\"code_hash\":\"0xe27e5fc974f95ee3384ddde53896cc29a12af1890b2a9e47eaa600a1edf7ee20\",\"code_size\":13411,\"count\":389},{\"address\":\"0xce9be0fabf05e4949b08ee38ad7147dd7827da74\",\"code_hash\":\"0xe282570c3d28ced1da593bb68a284c995e8b4807dc312532c5ba13c32910a795\",\"code_size\":5365,\"count\":388},{\"address\":\"0x3c11f6265ddec22f4d049dde480615735f451646\",\"code_hash\":\"0x4c3ba1e562a8a3a9b4fb66264fc4fddb7d2555aeb7dcb88f67e4d331c776c6b8\",\"code_size\":7222,\"count\":386},{\"address\":\"0x57e114b691db790c35207b2e685d4a43181e6061\",\"code_hash\":\"0x0da00342e67404f8e369602a4e3d60895f8d85a014242bf4effcb46b8079f00c\",\"code_size\":7713,\"count\":384},{\"address\":\"0x0fd04a68d3c3a692d6fa30384d1a87ef93554ee6\",\"code_hash\":\"0xd71fb245ca071d285cbe654dbae3b9c488c640fb0ca5232f2622c07dccd62ccf\",\"code_size\":6680,\"count\":382},{\"address\":\"0x26e550ac11b26f78a04489d5f20f24e3559f7dd9\",\"code_hash\":\"0x29012346dc4c5a7492d6dc6b2de2103ba21d63e78b483179a1f930ff764812f6\",\"code_size\":13646,\"count\":380},{\"address\":\"0xe07300c13d49b8560f51bb30b45c22ca7cd08af8\",\"code_hash\":\"0xc853a8ef2f5a40aede0b611cc5c6f589ba143a9b10ad5443f0b0a818d61dc1ac\",\"code_size\":12838,\"count\":380},{\"address\":\"0x5417994ae69e6ccb283bb6dbdba3006b3d3f9f95\",\"code_hash\":\"0x94cff9bada52ad345d048eb56b019d00a1a2cfab08bc0ba6bf65ef40cc097c9d\",\"code_size\":13254,\"count\":379},{\"address\":\"0x379cf95571d42092e5d16e689fdf19aacb536cbf\",\"code_hash\":\"0x1820b6ec91aa0ee396de14650c74cd2bf839927281f7634b54ea941e0751319d\",\"code_size\":24423,\"count\":378},{\"address\":\"0x93e8f92327bfa8096f5f6ee5f2a49183d3b3b898\",\"code_hash\":\"0x08455369321c61f596eeb3272369cb3ff9215ed0793ed5c0ca6acf850381acea\",\"code_size\":6766,\"count\":378},{\"address\":\"0x2f39d218133afab8f2b819b1066c7e434ad94e9e\",\"code_hash\":\"0x0f8c7f9c735ac0f3f35b1e78882a617380549def43faeca0d959d5aadd81dee4\",\"code_size\":9846,\"count\":377},{\"address\":\"0x3ced11c610556e5292fbc2e75d68c3899098c14c\",\"code_hash\":\"0xe8c164678c3db727a525befb079850436c9c54f5d599557e820c6b6930b5a566\",\"code_size\":13237,\"count\":376},{\"address\":\"0x85f6eb2bd5a062f5f8560be93fb7147e16c81472\",\"code_hash\":\"0x253724acc0b660a0abd4a6658fc9ee7e9aad759ef899d197e2d590337d692ffb\",\"code_size\":23038,\"count\":373},{\"address\":\"0x3708f5c9533557b1633c7a255ed385348488aeae\",\"code_hash\":\"0x31806a3591cc5062144a2ebb00a297a59ca2255be960bfd43b25276e57fd73eb\",\"code_size\":24512,\"count\":370},{\"address\":\"0x4d5f47fa6a74757f35c14fd3a6ef8e3c9bc514e8\",\"code_hash\":\"0x82c6d153799b3226525e3b7ec27b843ef44c5f6bca21fcf8b3c80db61ba64881\",\"code_size\":2400,\"count\":364},{\"address\":\"0xd4bc53434c5e12cb41381a556c3c47e1a86e80e3\",\"code_hash\":\"0xf999a5dc8ae9a3efb245f92a5fbf52ee1f8793f6f7dd1b629a44354fac373f4b\",\"code_size\":13835,\"count\":362},{\"address\":\"0xdb56e44fb8ee6c560a89a49841cfe0a07cc2fc39\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":362},{\"address\":\"0x663dc15d3c1ac63ff12e45ab68fea3f0a883c251\",\"code_hash\":\"0xfc1ea81db44e2de921b958dc92da921a18968ff3f3465bd475fb86dd1af03986\",\"code_size\":2141,\"count\":361},{\"address\":\"0xe67534a9f24cc000f467eaa17f920bf63b87a2cd\",\"code_hash\":\"0x0ad52aa045458d034e10b73045de66ac675d24bae3d4c2763ee0cd50d067d88c\",\"code_size\":13725,\"count\":361},{\"address\":\"0x039b9cf3407f4573b7d37530c42ef9c7db424fc8\",\"code_hash\":\"0x957256d2b6fd5b02c0f1dc4357c51be6c0293c321385b9cbbbc390201c78bd27\",\"code_size\":22142,\"count\":360},{\"address\":\"0xfbeedcfe378866dab6abbafd8b2986f5c1768737\",\"code_hash\":\"0xe4655f85874e38780979963e8a05a76c8054ccbfe833ab3a3022b821470f5fdb\",\"code_size\":20373,\"count\":356},{\"address\":\"0xbb2ea70c9e858123480642cf96acbcce1372dce1\",\"code_hash\":\"0xdd60d5461da3a779567301a7f52c4de2fd7d4c829b3e8de663bb2149be857275\",\"code_size\":22995,\"count\":356},{\"address\":\"0x055c48651015cf5b21599a4ded8c402fdc718058\",\"code_hash\":\"0xb3f5b74b00738101013d1f28101e8739220b34f00545b54288236742b47ef542\",\"code_size\":9974,\"count\":354},{\"address\":\"0x11b815efb8f581194ae79006d24e0d814b7697f6\",\"code_hash\":\"0x54f2b4c90d2939269a9d3ea8a3081dce03328c947d54bf3d98b2820922840b35\",\"code_size\":22142,\"count\":353},{\"address\":\"0xfcc153725d783148d6c972823aed1d28c5961456\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":351},{\"address\":\"0xecca809227d43b895754382f1fd871628d7e51fb\",\"code_hash\":\"0xb0d2c9fa3c61b001255f108dd48d6e8b2a9dad5a26f3302f3968a942eb7349e7\",\"code_size\":7013,\"count\":348},{\"address\":\"0x251805adb28090f84c7cdb7d9aa04f2ff1eaac00\",\"code_hash\":\"0x029f7e713e17156ab3baff1ffcac89cbed3e760cdea866995302cbaf1f44f265\",\"code_size\":22142,\"count\":347},{\"address\":\"0x7cdf68ce9a05413cbb76cb7f80eaf415a826e313\",\"code_hash\":\"0xa873be03126d64946ef888d2b014036c8271d0d781aec7f91ab85a56f6e1e44c\",\"code_size\":3592,\"count\":342},{\"address\":\"0x8d02988296949cd054623802c1115973a9afe307\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":341},{\"address\":\"0x378aba04ab5d07743f66f819875bf9e7d1e9b057\",\"code_hash\":\"0x140776b53f93c3276654c1011a35568c15f24d5b0873c003b2bbc538af776f71\",\"code_size\":13566,\"count\":340},{\"address\":\"0x5ff137d4b0fdcd49dca30c7cf57e578a026d2789\",\"code_hash\":\"0xc93c806e738300b5357ecdc2e971d6438d34d8e4e17b99b758b1f9cac91c8e70\",\"code_size\":23689,\"count\":339},{\"address\":\"0x00000000009e50a7ddb7a7b0e2ee6604fd120e49\",\"code_hash\":\"0x6f51c41d151c5962295abdc2a78f0b1b3589c176f5ba06c257571e7f40840d57\",\"code_size\":16842,\"count\":339},{\"address\":\"0xeead662a16c68fe58c2278c63201f2c739ee3317\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":337},{\"address\":\"0x6967e68f7f9b3921181f27e66aa9c3ac7e13dbc0\",\"code_hash\":\"0xd68a15f2c7798c6fe05cb18a5bf91f5f58a6c9701f1bb58896a585f69e1fee67\",\"code_size\":11293,\"count\":336},{\"address\":\"0x2e8135be71230c6b1b4045696d41c09db0414226\",\"code_hash\":\"0xa9695275563c31643f88b1d24a6ebddce09e67d39e0211b266fb4d905ae78858\",\"code_size\":11296,\"count\":333},{\"address\":\"0xbee3211ab312a8d065c4fef0247448e17a8da000\",\"code_hash\":\"0x18db792bd2e72644e3d7ccebd5aee3b036a7d0bf895cdd1a5de9a25d8d01fa2a\",\"code_size\":1855,\"count\":333},{\"address\":\"0x4dffeb6b78e57aab41d09e88c796e27cf314de6b\",\"code_hash\":\"0xe63298f4dbb4f2221af5d4ee869cf32fd9b69e1865ed65ae6a8547687bfc1a08\",\"code_size\":11853,\"count\":333},{\"address\":\"0x666e3ed8a1995b2f1972b0187983e75b0c8978ab\",\"code_hash\":\"0x84ca2a2c1382c7a255ee40dbe94467fde46eec9ade411f6e3fe1bd0dc025f529\",\"code_size\":12677,\"count\":332},{\"address\":\"0x9ec6f08190dea04a54f8afc53db96134e5e3fdfb\",\"code_hash\":\"0x1dc68bd8ed7951a5b2b72ebc76e0dea1878e6aca2dec51213ace96ca615d53b4\",\"code_size\":4038,\"count\":330},{\"address\":\"0x6747bcaf9bd5a5f0758cbe08903490e45ddfacb5\",\"code_hash\":\"0x352c89ecdee98622e5c49bc324ad08e843dc921b9e9a41aed08ad562c4bd2de6\",\"code_size\":3065,\"count\":328},{\"address\":\"0xba12222222228d8ba445958a75a0704d566bf2c8\",\"code_hash\":\"0x9eb70db20a41bfbf4b022fd070fa7f154b7c4aec98177120dde7a958384f4e66\",\"code_size\":24512,\"count\":328},{\"address\":\"0x004395edb43efca9885cedad51ec9faf93bd34ac\",\"code_hash\":\"0x1b8bf8377b8f2ac8b465146937427cd4f1942484a8d02b680c16dcca4c139785\",\"code_size\":22904,\"count\":328},{\"address\":\"0x74271f2282ed7ee35c166122a60c9830354be42a\",\"code_hash\":\"0x493edc50f59a806ab7429182076ede4f6669beac2199413deaece95e91b8e75c\",\"code_size\":14664,\"count\":327},{\"address\":\"0x45804880de22913dafe09f4980848ece6ecbaf78\",\"code_hash\":\"0xdcdc97bea5436354845afab66a9dc621e9ebe642db18e81a95c66b770f60bb3d\",\"code_size\":1506,\"count\":327},{\"address\":\"0x888888888889758f76e7103c6cbf23abbf58f946\",\"code_hash\":\"0x6e7e96418259651300fbc9b9035c0613bfefc601ddbd840ab39a068ab3ee4293\",\"code_size\":287,\"count\":327},{\"address\":\"0xefa45bac050a6d0f6e75ae397c3ba47fc5357f73\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":324},{\"address\":\"0x7458bfdc30034eb860b265e6068121d18fa5aa72\",\"code_hash\":\"0xe0aec0f6e7056bff3d64782ea95ddf88ec9beb40b65b0c854a698b248a6ca547\",\"code_size\":16328,\"count\":324},{\"address\":\"0xf63a8514f28e27ea235413b5a1cd4a21ca17ae19\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":324},{\"address\":\"0xf326e4de8f66a0bdc0970b79e0924e33c79f1915\",\"code_hash\":\"0xaea7d4252f6245f301e540cfbee27d3a88de543af8e49c5c62405d5499fab7e5\",\"code_size\":170,\"count\":324},{\"address\":\"0xcbb7c0000ab88b473b1f5afd9ef808440eed33bf\",\"code_hash\":\"0x91149353e08445ba77a52bf7e4cef919054027f4ad42812b4314bbaf2abd8b71\",\"code_size\":1550,\"count\":324},{\"address\":\"0x3a6baa53eca4a8a21df4457653e0d271186f1d21\",\"code_hash\":\"0x45c8e18e1b5ab327900d35ee926bf24e8ae59b7df1e0c6cf84ca543e62a0b1d1\",\"code_size\":6698,\"count\":321},{\"address\":\"0x8970d1d6452e8131a0ae749226b71797dd22e065\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":321},{\"address\":\"0x186c89e00df8431c0d6eef0147e4427318a89d9e\",\"code_hash\":\"0x361fbd2964500fe86384a5048d71220eec5bbe37403f316f332f5f6179ee7c19\",\"code_size\":2825,\"count\":320},{\"address\":\"0xbcb4e4bcc41ab1494a3eb3456ed4edb8da5d46e4\",\"code_hash\":\"0x19c04c2e5b49d6e2dbf4ffc4e911003986d162316e242ee9d9981b615889fe5b\",\"code_size\":14399,\"count\":319},{\"address\":\"0x962c8a85f500519266269f77dffba4cea0b46da1\",\"code_hash\":\"0x5ed8e77248cb5fe73383f31241d91248bfafcb424fdfbde4c5cc4e4f507761c6\",\"code_size\":12369,\"count\":317},{\"address\":\"0x5a98fcbea516cf06857215779fd812ca3bef1b32\",\"code_hash\":\"0x2dda0f3a6bee3e5768e41adb77021f8f5653897a1c65a5f106265b17fa2c299b\",\"code_size\":7440,\"count\":316},{\"address\":\"0xb5d85cbf7cb3ee0d56b3bb207d5fc4b82f43f511\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":315},{\"address\":\"0x41675c099f32341bf84bfc5382af534df5c7461a\",\"code_hash\":\"0x1fe2df852ba3299d6534ef416eefa406e56ced995bca886ab7a553e6d0c5e1c4\",\"code_size\":23579,\"count\":314},{\"address\":\"0x28d38df637db75533bd3f71426f3410a82041544\",\"code_hash\":\"0xc33461e7e6f10c7542806a54dbc50cd07691aca740cf07fa8b4affee6f96ad94\",\"code_size\":17281,\"count\":312},{\"address\":\"0xe421291d8163369a09215912229f601797891f3f\",\"code_hash\":\"0x0701c3736b23162598ba45e7eb791f7b1a57ac0538dea7cf3029fda96d791cd1\",\"code_size\":6925,\"count\":312},{\"address\":\"0x0f7dc5d02cc1e1f5ee47854d534d332a1081ccc8\",\"code_hash\":\"0x0c64f459646d908268ecf0375d15ffe35e03a25ef3ae61bb690ad4396a092147\",\"code_size\":7137,\"count\":312},{\"address\":\"0x1d695fa8543ae442a5adc6800386495ac3a08a9c\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":311},{\"address\":\"0x918cd3b96cb4b7dee53bf3fca696ee0bdd7ed5c2\",\"code_hash\":\"0x62561cb1cc3b8d054e07a861c84be8b04cad2bb52636972c4ab7025a0bce1d79\",\"code_size\":5266,\"count\":310},{\"address\":\"0x881ca06ff363996ce33df9950255e1cdd5b5530d\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":307},{\"address\":\"0xff00000000000000000000000000000000008453\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":307},{\"address\":\"0x00a0be1bbc0c99898df7e6524bf16e893c1e3bb9\",\"code_hash\":\"0x90d96618d7a67d85f5bb050a253b9fa38196909e8eb799626989b2461b23c4ba\",\"code_size\":3394,\"count\":307},{\"address\":\"0x6131b5fae19ea4f9d964eac0408e4408b66337b5\",\"code_hash\":\"0x06468e29bde518202bb725caad5eaaf8184c1b5338d8228e7c779248dbc9e2a3\",\"code_size\":18652,\"count\":305},{\"address\":\"0x53f5ff3aacd044d1634270f0c49141e750c1bb2b\",\"code_hash\":\"0xa70488405d4de038cf94ff5b3b625dd39dd2d103b07a5f5d80055c27a3b49a0c\",\"code_size\":5203,\"count\":305},{\"address\":\"0x66a1e37c9b0eaddca17d3662d6c05f4decf3e110\",\"code_hash\":\"0xb2a576c39621451a40ff068f8b5d70413ab0e138175b59913cc6df2639bfef13\",\"code_size\":1159,\"count\":303},{\"address\":\"0x44ff8620b8ca30902395a7bd3f2407e1a091bf73\",\"code_hash\":\"0x27051ea1ebfd8ba324b05851dcf11985d2c4d90b2921d310f93e996ee2b7335b\",\"code_size\":4810,\"count\":303},{\"address\":\"0xd3b9a1dcabd16c482785fd4265cb4580b84cded7\",\"code_hash\":\"0x336afe7a03c2bae22cebf0eb8633988c29c55f619fdcb09826229f3a0a2c5ce0\",\"code_size\":12958,\"count\":302},{\"address\":\"0xf939e0a03fb07f59a73314e73794be0e57ac1b4e\",\"code_hash\":\"0x7d33300cc75c4738e04e1f8c838b8b4fbb1c40d046131088463f70f9c712325f\",\"code_size\":3572,\"count\":302},{\"address\":\"0x88df592f8eb5d7bd38bfef7deb0fbc02cf3778a0\",\"code_hash\":\"0x18df49952ae206a293436515440bff85ba7a87d1ddf9ab0aab4e1fba63c11fbb\",\"code_size\":2779,\"count\":302},{\"address\":\"0x9e7ae8bdba9aa346739792d219a808884996db67\",\"code_hash\":\"0xbecbed5faf0a99174af105a9a6959a1375f44b16d71a03739a72fd886837a311\",\"code_size\":3600,\"count\":301},{\"address\":\"0x5d9270a159e2c2d13662d46d92a9164e9b1fdcb2\",\"code_hash\":\"0xb011e4f09ff54147ce1625b36af0015790e5783a809049dd1cd673c8acea142b\",\"code_size\":6007,\"count\":301},{\"address\":\"0x2c4c28ddbdac9c5e7055b4c863b72ea0149d8afe\",\"code_hash\":\"0x54cbdd610fb6f665de548d5c63ad0a76795d9cb9de844a8e5f4924a663412a8f\",\"code_size\":2341,\"count\":301},{\"address\":\"0xc92e8bdf79f0507f65a392b0ab4667716bfe0110\",\"code_hash\":\"0x500097799c1379a3728ed70b17de4132de2c07f6937b041c361deaade22b6a5e\",\"code_size\":4590,\"count\":301},{\"address\":\"0x498fe6625628053e4f97246161fab24aea3dec36\",\"code_hash\":\"0x26b489514a7e5db50a6e64b889065e2f3d1008457e6c4b865fe9a1d8349a3ebd\",\"code_size\":1009,\"count\":300},{\"address\":\"0x13ca409625aeab14a099ac2850ef19892fb717aa\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":300},{\"address\":\"0xe5caef4af8780e59df925470b050fb23c43ca68c\",\"code_hash\":\"0xe004b43bb2bb2309944250ecff1181d792555897e2fdde85385dfa30aa486b5f\",\"code_size\":5177,\"count\":299},{\"address\":\"0x4e1a00b849d971a07145aa17b25ae5d18c326cd5\",\"code_hash\":\"0xe80b4d2c853d9c6aec970a3db0d9a8b20d71556e0d7f61ad4ba9f233c342204e\",\"code_size\":5216,\"count\":294},{\"address\":\"0x1f573d6fb3f13d689ff844b4ce37794d79a7ff1c\",\"code_hash\":\"0xf1495e99bb63cf244df1a4e88c356e556d15497028e15842edccb032e3463bb4\",\"code_size\":4337,\"count\":292},{\"address\":\"0xadac14a0734d650c04ed9bd3bd2c3672e49fb126\",\"code_hash\":\"0xae702a593152fd7914f6eec9e4434f274888e7f2d0d2569b957d9fb8002b871b\",\"code_size\":5330,\"count\":292},{\"address\":\"0x15b25e3fb8419da4848a6f193bb9b43519d0d4ca\",\"code_hash\":\"0xdaffc34e2998af539641666e9d468979dbc35c9f128af11783f4ffa1aee6b096\",\"code_size\":87,\"count\":292},{\"address\":\"0x31b8dc8d6b0f7b1521962841e844c3e03f937504\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":291},{\"address\":\"0xc465cc50b7d5a29b9308968f870a4b242a8e1873\",\"code_hash\":\"0x7b287ee78288945f2c3ccb923d99243bbf70d6040de54ddeec372457739a4612\",\"code_size\":736,\"count\":290},{\"address\":\"0x7761883c68832bdd958bdd70fa415c0b313592bd\",\"code_hash\":\"0x6ca5bd3cdb7833aea973d04d24d41b7ec05a20843c45a6034ee66346f8c0815b\",\"code_size\":5213,\"count\":290},{\"address\":\"0x185477906b46d9b8de0deb73a1bbfb87b5b51bc3\",\"code_hash\":\"0x58bd246edbb55896f7b0b7c996c8219e01a934d5a68de2b7e2465f04bc8bae4d\",\"code_size\":11910,\"count\":290},{\"address\":\"0x307576dd4f73f91bb8c4a2edb762938e8e067d31\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":289},{\"address\":\"0xed8ab991e358ab6175079d089454a236f3ba290c\",\"code_hash\":\"0x6c4659793ca5bf9afafe5e599c0797ba59dcd6b292b3e985d04aa64af13cfc04\",\"code_size\":14019,\"count\":288},{\"address\":\"0xeb9622a578e557266007d5361142e85e9355cd86\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":288},{\"address\":\"0x40d09d1c989fcb3a68623fe1c1acb3b769e0d237\",\"code_hash\":\"0x0069b3b423623a81f810bf087a1224e83addc3a64680061e7bd1a49655da22b8\",\"code_size\":6284,\"count\":287},{\"address\":\"0x4d224452801aced8b2f0aebe155379bb5d594381\",\"code_hash\":\"0x86cc3092a7d90269edf27eb088feea0d277b77a77bfbbe06dff0a1b1affbfb9e\",\"code_size\":2244,\"count\":283},{\"address\":\"0xdcf63a49f5938fd3b673271015ecf1aa3a2ef154\",\"code_hash\":\"0x7cb274cf7646ded1b8f7b32035c504c9b9220d19df5ebb64619519ec07506276\",\"code_size\":5453,\"count\":283},{\"address\":\"0xa3e4bc24177fb99b373202c9bc012496d6592fe2\",\"code_hash\":\"0x6ab56e5e572095ba5871207ccabfaa1e9bf0b14f63bf274055e8ffb816f66d11\",\"code_size\":13656,\"count\":279},{\"address\":\"0x714c8b4864f3ef5191a0e001353ad5bca421ec08\",\"code_hash\":\"0xbbdb59dcc1fdb5728d5c7d54e8293aa18d640d91715cd3578f0f8d42cf56ddd5\",\"code_size\":14019,\"count\":279},{\"address\":\"0x6fcf753f2c67b83f7b09746bbc4fa0047b35d050\",\"code_hash\":\"0xe5b3b3660888f6c090cb5fa2279898044fb5c58e79e5f11b81bd40ddb571e9c4\",\"code_size\":6358,\"count\":277},{\"address\":\"0x4e7991e5c547ce825bdeb665ee14a3274f9f61e0\",\"code_hash\":\"0xc80a36f3ada09727300a955ca9bbef907b2bc4b3438fc90b826f03bf9be40baa\",\"code_size\":11231,\"count\":276},{\"address\":\"0xa3931d71877c0e7a3148cb7eb4463524fec27fbd\",\"code_hash\":\"0xa55c01da656c715cec222ea8fbd63256eb3ca6cbb7236071f2bcbff927f6e35b\",\"code_size\":170,\"count\":276},{\"address\":\"0x309831935ff311d35a44534216e776a41ad60274\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":275},{\"address\":\"0x49df8a5c87f00a0569fdd6f8c6b4b3734824f8ab\",\"code_hash\":\"0xd9769f7d5ef2baabcd2af57cbc4f45c053c1ded893a3e3340a72e2a1d4c23f08\",\"code_size\":13120,\"count\":275},{\"address\":\"0xa2cd3d43c775978a96bdbf12d733d5a1ed94fb18\",\"code_hash\":\"0x1692ad28a5a0c764f48e6984e64e0f92a6898c633f4c8a1c33169d75bcbf2719\",\"code_size\":6726,\"count\":275},{\"address\":\"0x00000047bb99ea4d791bb749d970de71ee0b1a34\",\"code_hash\":\"0xdfe1700faca15b9b53b879174904c8b0827ca49e609fcac34ef2035ae4f23c3b\",\"code_size\":23182,\"count\":274},{\"address\":\"0x455e53cbb86018ac2b8092fdcd39d8444affc3f6\",\"code_hash\":\"0x6693c23c81bda3634a9d116b9203d6efda678d59e558b9092a0c6e2fc85eba5d\",\"code_size\":8736,\"count\":272},{\"address\":\"0x1e68238ce926dec62b3fbc99ab06eb1d85ce0270\",\"code_hash\":\"0xee5a24499fe2fd51b5814c532969d7937d3d9589b366dacc59ea5e220c618b3b\",\"code_size\":2561,\"count\":271},{\"address\":\"0x0a7272e8573aea8359fec143ac02aed90f822bd0\",\"code_hash\":\"0xce199762f35b0abac79b903315669bfe07aeab73372612f830c3aee1ceb91c67\",\"code_size\":23342,\"count\":271},{\"address\":\"0x9600e1241e71447c3384872a4f55cb227ba7f5f8\",\"code_hash\":\"0xcb7becf9e750eb25aaa79ddbf7afadc69a6e2c3a458d81f6b3d92483eb779c17\",\"code_size\":12369,\"count\":271},{\"address\":\"0x7ac8ca87959b1d5edfe2df5325a37c304dcea4d0\",\"code_hash\":\"0xbdc71e07e9c22635b9da4b1c4fc2824eeecfba02c2c8e1f73df397d5fc867ced\",\"code_size\":13080,\"count\":269},{\"address\":\"0xa29c9a740de8194e4016747e9a04a84946ada0a5\",\"code_hash\":\"0xc8d6f1090eb902ab0b18f80e7fd7857d31630f87a544f83814c5fe19f132bed6\",\"code_size\":3669,\"count\":268},{\"address\":\"0xae922924362443723cbc13d3fe76b24c64153907\",\"code_hash\":\"0x6992cfd750ad2d7b3a50ab614eeb87ff83887b6944db157d42df4c3d3f29722a\",\"code_size\":5354,\"count\":267},{\"address\":\"0x482c4388e311c1e5a406f55141dd45f4848c1f36\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":266},{\"address\":\"0x8315177ab297ba92a06054ce80a67ed4dbd7ed3a\",\"code_hash\":\"0x8736329b580cfc0c0c39ee6700515e0bc51652afb614640db9e34a5d784933e8\",\"code_size\":2147,\"count\":265},{\"address\":\"0xd0269af7a380149cbeeea8d5379cae230ab0c8a4\",\"code_hash\":\"0x8f827f16de52dff13779b7ef29fb20bf697303ead50537938c5e4bac5afabfcf\",\"code_size\":20347,\"count\":263},{\"address\":\"0x02853143814a9456035e457a655b84465064239d\",\"code_hash\":\"0xf004bcd074c429cce37091e195973f5cfbcf5599704e3395fc2f047a95409346\",\"code_size\":1271,\"count\":261},{\"address\":\"0x8f68f4810cce3194b6cb6f3d50fa58c2c9bdd1d5\",\"code_hash\":\"0x7bead5c85d7bce833e933c384038394a1b5c0200d8a0f0b78e9b93e820d583c8\",\"code_size\":7863,\"count\":261},{\"address\":\"0x6140b987d6b51fd75b66c3b07733beb5167c42fc\",\"code_hash\":\"0x4c33ae1e20f18e474073820cff59cd93c953b3721014bbcdaa7aedf49ee04640\",\"code_size\":15651,\"count\":261},{\"address\":\"0xafb82ce44fd8a3431a64742bcd3547eeda1afea7\",\"code_hash\":\"0x56d2e8d67e238a675827e84917168cfbdd53ef30d198ea7f9eeefaaedee057db\",\"code_size\":11207,\"count\":260},{\"address\":\"0x6a393848f5d1b8e7dab45f3a7e01f9f0dc687242\",\"code_hash\":\"0x4bb6b7988e29cef460d115d47f4e93b03bc15a2440a7e27446eac7adcb80e348\",\"code_size\":11027,\"count\":260},{\"address\":\"0x6599861e55abd28b91dd9d86a826ec0cc8d72c2c\",\"code_hash\":\"0xb21b7d6fbab67601893f6996e761a4c9c358ae87a8fbf20d22cd251293fff848\",\"code_size\":845,\"count\":260},{\"address\":\"0x8e5689bde31b2a8d934138dfd7e7aa4db5a68ded\",\"code_hash\":\"0x354b26999e434f8256d168395315e1f51fc15bf49bf2e6bb58fc549bd597e7da\",\"code_size\":9743,\"count\":260},{\"address\":\"0x7b5ae07e2af1c861bcc4736d23f5f66a61e0ca5e\",\"code_hash\":\"0xb21b7d6fbab67601893f6996e761a4c9c358ae87a8fbf20d22cd251293fff848\",\"code_size\":845,\"count\":260},{\"address\":\"0x9a91ce023da7419e9bfb024432be22d6871cd2dd\",\"code_hash\":\"0x655bf248a7dc4dde7f1610d20bb4bbbb355540deb5c235f752fb1ba3439b9bab\",\"code_size\":7564,\"count\":259},{\"address\":\"0x4255c33130c02a3680c2295f91678dd8074d8327\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":259},{\"address\":\"0xa0ef786bf476fe0810408caba05e536ac800ff86\",\"code_hash\":\"0xdeda5fa422d4ba17192985bf24ad2d3572b4d30d3c8180145b925b3d1a70c02a\",\"code_size\":3937,\"count\":258},{\"address\":\"0xfe0c30065b384f05761f15d0cc899d4f9f9cc0eb\",\"code_hash\":\"0x6188ccdf81f390d37c61ea85ca893ec9f0af5135bc9eb1fd1337a4d7802ff794\",\"code_size\":7889,\"count\":256},{\"address\":\"0xc9e1a09622afdb659913fefe800feae5dbbfe9d7\",\"code_hash\":\"0x16f41184f797cb8f8918680df0ebf2a97cc3192aa6b104615f61096fc674f2aa\",\"code_size\":22337,\"count\":255},{\"address\":\"0xa59ba433ac34d2927232918ef5b2eaafcf130ba5\",\"code_hash\":\"0xe3486cf05f2c00e00767dec63eb46d309ac8b8e4d3b870a9c13c3de4605009f9\",\"code_size\":17973,\"count\":254},{\"address\":\"0x721c008fdff27bf06e7e123956e2fe03b63342e3\",\"code_hash\":\"0xab56b0c7eb7fb035bd2d8b560fa3c52fca081af10a0f204c88e6074cb974f0ef\",\"code_size\":24012,\"count\":254},{\"address\":\"0x337685fdab40d39bd02028545a4ffa7d287cc3e2\",\"code_hash\":\"0xb0f42a5e56d1dbfc5cc640c605c24152166bf2e08d68ce85affaf7b7d2fdd7ac\",\"code_size\":8840,\"count\":253},{\"address\":\"0x83948ee6e5125dde817aeb4e4f021b22afd231b9\",\"code_hash\":\"0x32e27a7c1e8e7e6d6fdeccb603ff4290ddcd8260fa6834ddb5dea885875b328e\",\"code_size\":6003,\"count\":253},{\"address\":\"0x40c57923924b5c5c5455c48d93317139addac8fb\",\"code_hash\":\"0x2aaadc77051849ed229924451464917ee65dbb52dbd5bb63fa088e60313b8875\",\"code_size\":3557,\"count\":252},{\"address\":\"0x7ca7b5eaaf526d93705d28c1b47e9739595c90e7\",\"code_hash\":\"0x7ba370b4c7e40d593cf56fa4da3f7a248bae279805faa07e2c121788bc243372\",\"code_size\":16066,\"count\":252},{\"address\":\"0x77edae6a5f332605720688c7fda7476476e8f83f\",\"code_hash\":\"0x6bec2bf64f7e824109f6ed55f77dd7665801d6195e461666ad6a5342a9f6daf5\",\"code_size\":2112,\"count\":251},{\"address\":\"0x544ba0f47b60a17f18fd18fe9db8d72f2f224b49\",\"code_hash\":\"0x1443a5272a02abb3ae2f46c618da91171bcfd3a307aaa29e16bf1005f8aa5ebf\",\"code_size\":14849,\"count\":251},{\"address\":\"0x3416cf6c708da44db2624d63ea0aaef7113527c6\",\"code_hash\":\"0x2ff673bacc60a73fc85c678888296c6bce3de2a9d7475c032fe7aa6e0eacba86\",\"code_size\":22142,\"count\":251},{\"address\":\"0x35d1b3f3d7966a1dfe207aa4514c12a259a0492b\",\"code_hash\":\"0x808b98f6475736d56c978e4fb476175ecd9d7abdab0797017fc10c7f46311a59\",\"code_size\":13493,\"count\":250},{\"address\":\"0x604485c32ed7628f12b1bde85ef6ef717409704b\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":249},{\"address\":\"0xbbbbbbbb46a1da0f0c3f64522c275baa4c332636\",\"code_hash\":\"0x078cb775ed3c46aba9b3d016fcb3e0853336a64133a845f26b62ac1497a8891f\",\"code_size\":2491,\"count\":248},{\"address\":\"0x57f1887a8bf19b14fc0df6fd9b2acc9af147ea85\",\"code_hash\":\"0x15557327bea19f454db3ec207da1a440b6d9318af56e52e2a3eba440d4b62e0a\",\"code_size\":10542,\"count\":248},{\"address\":\"0x28c6c06298d514db089934071355e5743bf21d60\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":247},{\"address\":\"0xa9d1e08c7793af67e9d92fe308d5697fb81d3e43\",\"code_hash\":\"0xa4497f4402e130b2043ab62721736fb12182e744d7df2c905a5ac98ab04e6b32\",\"code_size\":6889,\"count\":246},{\"address\":\"0xcf0c122c6b73ff809c693db761e7baebe62b6a2e\",\"code_hash\":\"0x62f6a27beef672562e73fb0f3dcc4efb0d16b16cf4daeddb5c9cd8e17f58d6f8\",\"code_size\":8007,\"count\":244},{\"address\":\"0x4a220e6096b25eadb88358cb44068a3248254675\",\"code_hash\":\"0x37a685d7f4eca5e68dbfeae3fecdd7ec467dcac37d5c0c90591de54d5ea525c1\",\"code_size\":5155,\"count\":242},{\"address\":\"0xa6ad5a5bf1cde56cca1414e4623249f84c460fb6\",\"code_hash\":\"0x26b489514a7e5db50a6e64b889065e2f3d1008457e6c4b865fe9a1d8349a3ebd\",\"code_size\":1009,\"count\":240},{\"address\":\"0x46c51d2e6d5fef0400d26320bc96995176c369dd\",\"code_hash\":\"0xeb86fbb892fef549234712141bc3516d70fef3d18e7a5db67b41a6fedf32b45d\",\"code_size\":9514,\"count\":238},{\"address\":\"0x35fa164735182de50811e8e2e824cfb9b6118ac2\",\"code_hash\":\"0x0b58ec11caee361ca0c1e484c12f6e7177634222186f7639bd9183dfee9d2278\",\"code_size\":845,\"count\":238},{\"address\":\"0xaea46a60368a7bd060eec7df8cba43b7ef41ad85\",\"code_hash\":\"0x6e3e52d423c05528838baee3490fd92e429812e3df65ad7d39e6cba473f8b2ed\",\"code_size\":6318,\"count\":237},{\"address\":\"0x3a23f943181408eac424116af7b7790c94cb97a5\",\"code_hash\":\"0x6700acf5ccb383cb2cff102ba577f0bce216a5898e28adbdbbb5665916a65b49\",\"code_size\":24305,\"count\":237},{\"address\":\"0x58b6a8a3302369daec383334672404ee733ab239\",\"code_hash\":\"0x6dc0459fd591c27aba456e5a097e3d34c3f82d5775621dd9654efc1065f1b98a\",\"code_size\":3429,\"count\":236},{\"address\":\"0x974caa59e49682cda0ad2bbe82983419a2ecc400\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":234},{\"address\":\"0x260d708a2645c783fdbcdb13cf22930fa743f23f\",\"code_hash\":\"0x4aef5f237f06cb8b9d7f1470a24ed2fe6c7fe0e472dbde05844e53c40e4befa0\",\"code_size\":15389,\"count\":234},{\"address\":\"0x37df1f4bb7c05a6e5383d3de926eb1691e9c9e06\",\"code_hash\":\"0x14abb95bc48c253ec515902a71be81e9ba7c2b009bfd39b45f4fddc7b2dcf117\",\"code_size\":6162,\"count\":234},{\"address\":\"0x10ab8d6af85fb18afb7869e0f8d003cc6014a0c1\",\"code_hash\":\"0x0e5b3dad59feab14cea2a8de4494a6ab2064baa5c04541a88bc057f6f9a8c3cc\",\"code_size\":5337,\"count\":232},{\"address\":\"0x8cfc184c877154a8f9ffe0fe75649dbe5e2dbebf\",\"code_hash\":\"0x445ae892e621a2706ec2ea2e2bf116623feb7aba8efc7b81bb7ab0c8a88d9d97\",\"code_size\":12788,\"count\":232},{\"address\":\"0x556b9306565093c855aea9ae92a594704c2cd59e\",\"code_hash\":\"0x2639400c39e352e43ee2ce7027e932a47519a7037b663011a86b781039440aa8\",\"code_size\":22845,\"count\":231},{\"address\":\"0xbd5cf5c53a14a69fff27fe8b23e09bf76ba4de58\",\"code_hash\":\"0xb1cc4dcedf527205d09c1bf2c399ee9fcc7cdf82e91e6bfe36d2fc20af3d7b83\",\"code_size\":12848,\"count\":231},{\"address\":\"0x31a9b1835864706af10103b31ea2b79bdb995f5f\",\"code_hash\":\"0xc207f6fc07b111370c4d536c8c36de974367fd8876c867e390a5535cb120dbd7\",\"code_size\":8360,\"count\":231},{\"address\":\"0x1d8f8f00cfa6758d7be78336684788fb0ee0fa46\",\"code_hash\":\"0x06ca4c6dae1ac07c034c4db6ac4c2f7179a02f66cf2c266a383e090b3b407dae\",\"code_size\":11270,\"count\":230},{\"address\":\"0x52ae12abe5d8bd778bd5397f99ca900624cfadd4\",\"code_hash\":\"0xb06457f8a23744160e3ef810b651706aec059c640cc2fde57bd4860a01ba3eb2\",\"code_size\":3012,\"count\":229},{\"address\":\"0x0000a26b00c1f0df003000390027140000faa719\",\"code_hash\":\"0x0e0a5f7bfe4bf9760a537b808095ac2a9e37c7755f7326691c11c216918166bf\",\"code_size\":1250,\"count\":229},{\"address\":\"0x8fffffd4afb6115b954bd326cbe7b4ba576818f6\",\"code_hash\":\"0xbd6f524cdc4268b6bd1bb6f77a8821faeea9c52ee9e0afa0b6d948ce82c966c2\",\"code_size\":9571,\"count\":229},{\"address\":\"0x17c1ae82d99379240059940093762c5e4539aba5\",\"code_hash\":\"0xa9695275563c31643f88b1d24a6ebddce09e67d39e0211b266fb4d905ae78858\",\"code_size\":11296,\"count\":228},{\"address\":\"0x48817bf2fbb3ec44e6f617a7551000d5a6cb29ea\",\"code_hash\":\"0x40c50f339949824c261cb93a15c2277430e2fd1b3fd2773b2d928c58caba66e7\",\"code_size\":5452,\"count\":228},{\"address\":\"0xb71b4e954f132f3770d9bde0d1d5eb50c32147ef\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":227},{\"address\":\"0x000714bad5097985304ae187a64c80bd07e8694d\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":224},{\"address\":\"0x1ce9345d16cd3f9332438fc2c18dfa6556c5658e\",\"code_hash\":\"0x6ab3ecf42c3f3c1ff09df11bbc57f203972aa14900f0912a4360d42b60eb79d8\",\"code_size\":3427,\"count\":223},{\"address\":\"0x63543307e2448726f369e55a8e5024705cac3b6e\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":222},{\"address\":\"0x7efc0ab2a3b97a242477858b1ea34bdea0d25dc1\",\"code_hash\":\"0x0ec6b4fe3f414bda085b7041fd6cd319feba3a23a31e19303de004dccb3ab6bd\",\"code_size\":13254,\"count\":221},{\"address\":\"0x5c9321e92ba4eb43f2901c4952358e132163a85a\",\"code_hash\":\"0x61e661ef9a83ca260d1ec2ecc7c94ab7c5f3030566864e3da3057701652610b7\",\"code_size\":22068,\"count\":220},{\"address\":\"0x8a71a28ebf12719dcc99e5795119d542c9a3ef75\",\"code_hash\":\"0x3b923cd37eb7750062bdaef1351ce506937fd4ca0e2711afba8ff1952a208801\",\"code_size\":6617,\"count\":220},{\"address\":\"0x960692640ac4986ffce41620b7e3aa03cf1a0e8f\",\"code_hash\":\"0xfdea6bc889b90f4a514e817959d419876281302b1678d4ffb97eb90d77b6b912\",\"code_size\":11787,\"count\":220},{\"address\":\"0x76887cb94cf29ec539b3219ba62104be04f26a5c\",\"code_hash\":\"0x379dab502fc75ea045c950ce4f7fd9e8191f68cc26b8f539819590e49215bde1\",\"code_size\":6791,\"count\":219},{\"address\":\"0x76edf8c155a1e0d9b2ad11b04d9671cbc25fee99\",\"code_hash\":\"0x8f4f49589632b2e4fd46bb71468d73f4e61ac129e8eebd66b43d7d888b479959\",\"code_size\":24482,\"count\":218},{\"address\":\"0x000000000000aaeb6d7670e522a718067333cd4e\",\"code_hash\":\"0xf81224da2c9fa1872376316fdc140b4b1e9dbf3f4579f37e9671575af143b617\",\"code_size\":14408,\"count\":218},{\"address\":\"0x00000000000c2e074ec69a0dfb2997ba6c7d2e1e\",\"code_hash\":\"0xd6bfd5d6f1384a1f6ea57b8a8412de5552f138d42021cf7c4941e33206f529e4\",\"code_size\":5346,\"count\":216},{\"address\":\"0x77fb8335d4f1bded5578780b71c3ae2e3e64520d\",\"code_hash\":\"0x05830d1b14b734f32f32f93758c139d26bc674f6fd5fa30c1530f2340bdd8b8f\",\"code_size\":1704,\"count\":216},{\"address\":\"0x136de0af045de8c7c8ec5fa50be81c11492cf8bc\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":216},{\"address\":\"0x9f36ee33fd56c7d9a78facd3249c580b1ca464a2\",\"code_hash\":\"0x6a44a98613e0b06f08d5e73c796796f68eb95d09d50051aba74267ae7c2fbbf7\",\"code_size\":17857,\"count\":216},{\"address\":\"0x8fafae7dd957044088b3d0f67359c327c6200d18\",\"code_hash\":\"0x7f3322c27435e1b60bd551276e6e8b868108344fe29a8a5427057f822c203f9a\",\"code_size\":17973,\"count\":216},{\"address\":\"0x4239b4ccea1e6e0e6eafbc0065468c9897dc5843\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":216},{\"address\":\"0x5e860ba4545995381bd4cc0272e7de7e3ea881c1\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":215},{\"address\":\"0xef9553385469927dc1526927f1a96a58dde79f2a\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":214},{\"address\":\"0xb5184264d0c1868cc1180a0f6c683eed35f03f7a\",\"code_hash\":\"0xf263752b70d1a748cd5b9f6f00fd90e20806a7a4d399c5e4e48059db430d5f0d\",\"code_size\":1292,\"count\":213},{\"address\":\"0x98c23e9d8f34fefb1b7bd6a91b7ff122f4e16f5c\",\"code_hash\":\"0x82c6d153799b3226525e3b7ec27b843ef44c5f6bca21fcf8b3c80db61ba64881\",\"code_size\":2400,\"count\":213},{\"address\":\"0xa77c6cc807bae52bc921abcafbe6c36c8ef76279\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":212},{\"address\":\"0x40d16fc0246ad3160ccc09b8d0d3a2cd28ae6c2f\",\"code_hash\":\"0xdd51428dd1ef13362e52bfc1689ed8e011730e6c6d5b50aaf96165ccd7bf0172\",\"code_size\":8046,\"count\":212},{\"address\":\"0xa43fe16908251ee70ef74718545e4fe6c5ccec9f\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":212},{\"address\":\"0xc855d842ff0af97b0d18cc81eecbb702ea1a0706\",\"code_hash\":\"0x1ea551008ae46d3d06ddbec0c2aed880e0a518555f1fa35742a36476214e64ac\",\"code_size\":13009,\"count\":211},{\"address\":\"0x97ad75064b20fb2b2447fed4fa953bf7f007a706\",\"code_hash\":\"0x2c983535d73c8b9a76f6ff168afb91addb1b7dcd4e403c5b782a65b09a13b4fa\",\"code_size\":3032,\"count\":211},{\"address\":\"0x41545f8b9472d758bb669ed8eaeeecd7a9c4ec29\",\"code_hash\":\"0x330b9f1afee9d71fb2ee42927a996a39889a45323883a66a194e210ab850d111\",\"code_size\":680,\"count\":211},{\"address\":\"0x6d6620efa72948c5f68a3c8646d58c00d3f4a980\",\"code_hash\":\"0xef22a8fb9189866e656f799d686af5c5cdeb0ab47e1baa4f636f6d8253af970c\",\"code_size\":21135,\"count\":211},{\"address\":\"0x2fa878ab3f87cc1c9737fc071108f904c0b0c95d\",\"code_hash\":\"0xc198c2dd8656b9c98c4da0282125e803af3464670db395ae0f07146b05411e2d\",\"code_size\":992,\"count\":210},{\"address\":\"0x95b303987a60c71504d99aa1b13b4da07b0790ab\",\"code_hash\":\"0xc7a9119fb91d05a5b91430ba5afe903805f106a5fa0ab701ecfe7cf5e9046041\",\"code_size\":4917,\"count\":210},{\"address\":\"0xbc530bfa3fca1a731149248afc7f750c18360de1\",\"code_hash\":\"0x40802296c24793f9d86e9e09d87c4e03606856c98cbdd749d6499bea4467d07c\",\"code_size\":6565,\"count\":210},{\"address\":\"0x9e72a0e219cff0011069ae7b0da73fa26280f41b\",\"code_hash\":\"0x148b416aa6f788aa6d7624421a754ee089726b54d7dd2714b6ad06f1e4d38967\",\"code_size\":7420,\"count\":209},{\"address\":\"0x9ba0cf1588e1dfa905ec948f7fe5104dd40eda31\",\"code_hash\":\"0xdca3eba4af34dc721459027718f7301433a6da2943a10164593af2afbacfde72\",\"code_size\":10261,\"count\":209},{\"address\":\"0xeba88149813bec1cccccfdb0dacefaaa5de94cb1\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":209},{\"address\":\"0x56072c95faa701256059aa122697b133aded9279\",\"code_hash\":\"0xf0d16b91bddf2f60961cc39af64a8f92781c3239fc96021cf7fa925abaf22ccc\",\"code_size\":4852,\"count\":208},{\"address\":\"0xe5dcdc13b628c2df813db1080367e929c1507ca0\",\"code_hash\":\"0x42369ef63736a4e8a1d2a615bd8b54667e469388ce0f791c1e44ce8adcf44f54\",\"code_size\":14388,\"count\":208},{\"address\":\"0x589dedbd617e0cbcb916a9223f4d1300c294236b\",\"code_hash\":\"0xe3486cf05f2c00e00767dec63eb46d309ac8b8e4d3b870a9c13c3de4605009f9\",\"code_size\":17973,\"count\":208},{\"address\":\"0x163f8c2467924be0ae7b5347228cabf260318753\",\"code_hash\":\"0x24d2822bd010463716f27a942c37c6f5da08c3393c86f5c6b179fafcbb3b6ae2\",\"code_size\":4738,\"count\":207},{\"address\":\"0xa1bc65ecf8bc7b2faa22c53bcc49b0376da3845a\",\"code_hash\":\"0x3759c8e9d5215db547780cd6ed178eeecef7caf048b58b35886764de3ced507e\",\"code_size\":16878,\"count\":206},{\"address\":\"0x57240c3e140f98abe315ca8e0213c7a77f34a334\",\"code_hash\":\"0x566b7d85dcbaf4f0433cfffaa6b6ab13daa045a77d895e5412b1b8347a54bb04\",\"code_size\":8837,\"count\":206},{\"address\":\"0x8236a87084f8b84306f72007f36f2618a5634494\",\"code_hash\":\"0xf8be24ba1828343d015f94b6814630b0d4771a95598e3257dfe5ba87fbce3f71\",\"code_size\":1159,\"count\":206},{\"address\":\"0x9a10da8ce77f26231860764a2caab36e70584c4b\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":206},{\"address\":\"0xf411903cbc70a74d22900a5de66a2dda66507255\",\"code_hash\":\"0x56a25125b2173b35701d8aeb0e8483ad43a2a32de075fb7cd8b2ffd75e9925b6\",\"code_size\":18820,\"count\":205},{\"address\":\"0xbc7ce7b6b5437d7d715fbb1cc7b4ec12399c5516\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":205},{\"address\":\"0xd31a59c85ae9d8edefec411d448f90841571b89c\",\"code_hash\":\"0x857e34d35e463e3728698b32ce340557501e07a7be9bf2c4edbd1301438be916\",\"code_size\":831,\"count\":204},{\"address\":\"0x6eb5e9d093881210291f32ed7072b0044d474baf\",\"code_hash\":\"0x3ba416f1326910b6f87e271dc9cc540ad0a29ebb1b070a40cc9faed52e9c6b0a\",\"code_size\":13038,\"count\":203},{\"address\":\"0xad01c20d5886137e056775af56915de824c8fce5\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":203},{\"address\":\"0xf94e7d0710709388bce3161c32b4eea56d3f91cc\",\"code_hash\":\"0xe3dfc795982728d8b0ab0b8f4a86cccda8cb1eb4cd144ae922861cdbe5511e9e\",\"code_size\":10211,\"count\":203},{\"address\":\"0xbdd0cfa23ed41f0002398d1a1e1a600a83b43870\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":202},{\"address\":\"0x1eadcbd05fec7fb74b7ac51765fa79a3eea97abb\",\"code_hash\":\"0x7da32bffe5fea3fac13c6dbd37fb3cb75b25a8136e490f8de5004864593ce453\",\"code_size\":5797,\"count\":202},{\"address\":\"0xabd4e5fb590aa132749bbf2a04ea57efbaac399e\",\"code_hash\":\"0xc7c04633def7306fd3efda7656dce97c3207fa5e4bf4a943ea9be270683aa1bc\",\"code_size\":7569,\"count\":202},{\"address\":\"0x00000000219ab540356cbb839cbe05303d7705fa\",\"code_hash\":\"0x6c029a231254fadb724d63be769f75eedd66362df034a3e663252b49d062a666\",\"code_size\":6358,\"count\":202},{\"address\":\"0xa2d81172ae9492890973d5ac79ca0e03e7a41d5e\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":201},{\"address\":\"0x14fee680690900ba0cccfc76ad70fd1b95d10e16\",\"code_hash\":\"0x2e92e1fe6db9b5ab985295d2d34275c4121f8f65724eb0eec85c7d662debc2f1\",\"code_size\":15612,\"count\":199},{\"address\":\"0x52dac7a7e63e72f0e9115901183f5f6db0b5ae53\",\"code_hash\":\"0xc1d1980ea0b3c37372d9e1c02fa30d6d428e633bd36b7b7347e3303b9dea284b\",\"code_size\":6743,\"count\":199},{\"address\":\"0xbc1e6bb54b987e47b011d17ee6c09dfbe414aa84\",\"code_hash\":\"0xfbb8aa15a31f0bd775f66cc8f21e504710a959a37bde8e350f6c6e8ff6584db8\",\"code_size\":24004,\"count\":198},{\"address\":\"0x4313c378cc91ea583c91387b9216e2c03096b27f\",\"code_hash\":\"0x4d9be648c5bf39973670d9f8b481d5d0b971e6a2db2deccc6b98cde21c5dd83e\",\"code_size\":2227,\"count\":198},{\"address\":\"0x031f1ad10547b8deb43a36e5491c06a93812023a\",\"code_hash\":\"0x42290cbae7bd289ad5b81422508cf767bdd8427c81e166768650e5ce83dfd1ae\",\"code_size\":2352,\"count\":198},{\"address\":\"0x0aa784f3b9db2166b0ecc367bbcca89ea11a2036\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":196},{\"address\":\"0x6c99f059372afcb8a972bd5f30a92e6cdcfde1a1\",\"code_hash\":\"0x26b489514a7e5db50a6e64b889065e2f3d1008457e6c4b865fe9a1d8349a3ebd\",\"code_size\":1009,\"count\":195},{\"address\":\"0x9d39a5de30e57443bff2a8307a4256c8797a3497\",\"code_hash\":\"0xadc4989c92fab525a1a22b3f60f5b61b77f9eb11b693e8afeba5736ea4b502e3\",\"code_size\":17299,\"count\":195},{\"address\":\"0x39053d51b77dc0d36036fc1fcc8cb819df8ef37a\",\"code_hash\":\"0x073c6ec3b11b1c79a5e70af69992743e1f9857ca449d45cf954c164b0e464a9a\",\"code_size\":2151,\"count\":193},{\"address\":\"0xa75112d1df37fa53a431525cd47a7d7facea7e73\",\"code_hash\":\"0xc731f189ddab7bf1e9c5ae7fe95df9315344da914ddb0423a433d87776502e83\",\"code_size\":24144,\"count\":193},{\"address\":\"0x292fcdd1b104de5a00250febba9bc6a5092a0076\",\"code_hash\":\"0x9302f2f590375cd85de3b3481b12ea3b26576eb8c408158689206ee8b7e80321\",\"code_size\":14654,\"count\":193},{\"address\":\"0x8eea6cc08d824b20efb3bf7c248de694cb1f75f4\",\"code_hash\":\"0x90d96618d7a67d85f5bb050a253b9fa38196909e8eb799626989b2461b23c4ba\",\"code_size\":3394,\"count\":193},{\"address\":\"0x0c9a3dd6b8f28529d72d7f9ce918d493519ee383\",\"code_hash\":\"0x79e2fb648ff1e9d2fd847f9ce75de24dd41b4b5f39beddc8f595f20fd31bbfb0\",\"code_size\":21989,\"count\":192},{\"address\":\"0x83d6fa7f8904299f4a9499fe83b6ae3f21ffba57\",\"code_hash\":\"0x06a0f9c85dd4026edf58acd776d03199c68ba66dfc4c902a0b25baf30859602c\",\"code_size\":4182,\"count\":192},{\"address\":\"0x1917fa69f938acc226fd5ad6a05ae7c1b6d3488f\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":192},{\"address\":\"0x68bbed6a47194eff1cf514b50ea91895597fc91e\",\"code_hash\":\"0xbdc6c5c68994b372cb69988917bdb029638ce9a92e420ff1b22fadeaa2b8304f\",\"code_size\":9773,\"count\":191},{\"address\":\"0xf67c72a67f482d72be981152e252198248855c64\",\"code_hash\":\"0x5bf1d2ab9fee43715d122736b0c3f53449e0e8e8137f8f0aaa8361ebb095294d\",\"code_size\":1253,\"count\":191},{\"address\":\"0x2623edc6008d057786a6bf9dd34185dcddebbf2f\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":191},{\"address\":\"0xb8c35e66b4faafdccace27e8b5062f45cb381e31\",\"code_hash\":\"0x6c3a75b7d6b6cd18885c43741b59c3aabf9ac9a11b165e5ad31bebc218f8f94f\",\"code_size\":8266,\"count\":191},{\"address\":\"0xe3478b0bb1a5084567c319096437924948be1964\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":191},{\"address\":\"0x68749665ff8d2d112fa859aa293f07a622782f38\",\"code_hash\":\"0xfc1ea81db44e2de921b958dc92da921a18968ff3f3465bd475fb86dd1af03986\",\"code_size\":2141,\"count\":190},{\"address\":\"0x1fd8af16dc4bebd950521308d55d0543b6cdf4a1\",\"code_hash\":\"0x70d105ee0e9b857244d87521bbcef34cfb4cbd600592af61bbc7c76180648d61\",\"code_size\":10137,\"count\":190},{\"address\":\"0x4c0d2c74a8d26f1e4f5653021c521f5471f9e566\",\"code_hash\":\"0xfef8298b0af4bdb54b3a16352be060d3a0b78f07dae12324993a693cdb72e04b\",\"code_size\":8273,\"count\":190},{\"address\":\"0x7d0ccaa3fac1e5a943c5168b6ced828691b46b36\",\"code_hash\":\"0x9489905b44214509863077648b57742467371648b47b3394e97a103e566d06ce\",\"code_size\":18503,\"count\":189},{\"address\":\"0xb20a278de0f0ebf7794a8c212c0f3bca67768722\",\"code_hash\":\"0x3db0c8dea07586448be8dfb57f8e52e50a9a6438cce273afa3d89b3ccc743a69\",\"code_size\":19824,\"count\":189},{\"address\":\"0xf6e72db5454dd049d0788e411b06cfaf16853042\",\"code_hash\":\"0xd351b6020bfa630893dc7a52b63c894643d52de5643d5d81bfc0305eab665b2a\",\"code_size\":8414,\"count\":188},{\"address\":\"0x0000000000a39bb272e79075ade125fd351887ac\",\"code_hash\":\"0x69402eb12ab1b32c03a5235b68a648dc2ab34240007355668139fa528e1fa885\",\"code_size\":676,\"count\":188},{\"address\":\"0xdace1121e10500e9e29d071f01593fd76b000f08\",\"code_hash\":\"0x562d66133d0438f620f40c172735ab32a5117b1f217c2a0f3a297d70f25fb5f4\",\"code_size\":24428,\"count\":188},{\"address\":\"0x01a656024de4b89e2d0198bf4d468e8fd2358b17\",\"code_hash\":\"0xc55f39a08e23dffc91cef47002cc166333fd417c96cda40a6e35c92aac5a0f77\",\"code_size\":7001,\"count\":188},{\"address\":\"0xdef1ca1fb7fbcdc777520aa7f396b4e015f497ab\",\"code_hash\":\"0x51d6055ccca90f40b3f0f1ae25f4da40e3d5b03fcdf93c519c6b1fc233e64bd5\",\"code_size\":7345,\"count\":187},{\"address\":\"0x6a000f20005980200259b80c5102003040001068\",\"code_hash\":\"0x4cab3b67517db4f72f0915830b95a55f87bd2a2e3e11821c34abbdc3969ea60e\",\"code_size\":24562,\"count\":187},{\"address\":\"0x87e3ba929c71c0e28fc1c817d107a888a59c523e\",\"code_hash\":\"0x454bfb64054f3205cfc9c66cc48ec0fbde5b329839e7feac4ac306b91b1c7c6e\",\"code_size\":12163,\"count\":185},{\"address\":\"0x8238884ec9668ef77b90c6dff4d1a9f4f4823bfe\",\"code_hash\":\"0x5e4dcb0bb1910f6429e5fe91678990088a51c6d1cfe1b31d05fb9d948cc7867c\",\"code_size\":708,\"count\":185},{\"address\":\"0x2218f90a98b0c070676f249ef44834686daa4285\",\"code_hash\":\"0x851c7bea863541332656d77df23c217c5013416390dcefce9992f95655446c1e\",\"code_size\":9299,\"count\":185},{\"address\":\"0x17679a453a80496bd0774e568ff933d007be649b\",\"code_hash\":\"0x4f4759f9d458baf772dfae19993b1a25c65fdbc190a23e9ee47d91a1e82d019a\",\"code_size\":11001,\"count\":184},{\"address\":\"0xbbbbbbb520d69a9775e85b458c58c648259fad5f\",\"code_hash\":\"0x7b3fa17baf5436d40cf5e6d9b517f2077826c4720d241043b3188764a8c8f408\",\"code_size\":24163,\"count\":184},{\"address\":\"0x253c6e08db15e2912cf3afe5a89f2a7a4d8f2784\",\"code_hash\":\"0x0a54ba33e1e226b7acc27947d22a7465becae9149a9216b0fd16de6eb1226575\",\"code_size\":3672,\"count\":184},{\"address\":\"0x552f41b432e04bd1150a859f8f68e4cb558ea51c\",\"code_hash\":\"0xe6d651040f56c00d3a0ec88b6dd5a0f57b123c350431571c8712f642d64842d6\",\"code_size\":11403,\"count\":184},{\"address\":\"0x5ebb3f2feaa15271101a927869b3a56837e73056\",\"code_hash\":\"0x48c7051a1b6982681eb159bdabad9a424c5821b8fb5aec06a92fbf7804adf487\",\"code_size\":3547,\"count\":183},{\"address\":\"0x4e9c57fd2bd0f47c43f2d62642c1b05894fb9ed0\",\"code_hash\":\"0xd87979de45663a53ad92d50eced97d8eab0b9e3e005d5260939c891178b8f608\",\"code_size\":7607,\"count\":183},{\"address\":\"0x7d1afa7b718fb893db30a3abc0cfc608aacfebb0\",\"code_hash\":\"0x8b0a83a4cd51e97515c5898d296297d96c5377467369ec0ff432948c3ae995f2\",\"code_size\":2947,\"count\":183},{\"address\":\"0x3e2e243c55914de3f8a0e29cde30902465f02e3d\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":182},{\"address\":\"0x1837ea7a62cae7d31c4fae13bec592845d73216e\",\"code_hash\":\"0xa2108074f154a8f19f37c71117335a0309284de34ab1d692bb8b78c07fcbbbad\",\"code_size\":13566,\"count\":182},{\"address\":\"0x7bdb61d76ab2237102a7203da24816ef0a6408f6\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":181},{\"address\":\"0x812ba41e071c7b7fa4ebcfb62df5f45f6fa853ee\",\"code_hash\":\"0xa1656fc40991d294684b21d3f38ee0be82a853e85b3bb6e12c4baff1159ccfed\",\"code_size\":15815,\"count\":180},{\"address\":\"0x9f4c278a83928494bdaea44be5afc99015076776\",\"code_hash\":\"0x5e2aa3a803620e635475ab838930d1922d06f56337bcf9b340b25d22553d5439\",\"code_size\":2113,\"count\":180},{\"address\":\"0xbbd2ca33ff2988c028447de1c5c668db1ed94cfa\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":180},{\"address\":\"0x06a9ab27c7e2255df1815e6cc0168d7755feb19a\",\"code_hash\":\"0x26e751b9fd68f5207c6d2b35f2871bc22c1b05e9d20368c10257dc04ca67f46e\",\"code_size\":170,\"count\":179},{\"address\":\"0x497b13f9192b09244de9b5f0964830969fb26f07\",\"code_hash\":\"0xdeef7942fdc478b6d714eeb1a59df8f2dee6e7cbd77e1287faa55acb399df6f4\",\"code_size\":23121,\"count\":179},{\"address\":\"0x4e030b19135869f6fd926614754b7f9c184e2b83\",\"code_hash\":\"0xc0b94663b3e98be93f55543115dfecf1449e6c4b758ea2c1e9405937f17a07ec\",\"code_size\":3893,\"count\":179},{\"address\":\"0x948a420b8cc1d6bfd0b6087c2e7c344a2cd0bc39\",\"code_hash\":\"0x54a3a9ef2209a3bc2f00d3ca72d5b987776a6cb8db96acefb244610d08de0174\",\"code_size\":2115,\"count\":178},{\"address\":\"0xbbc025558a2b6baab09bfc06a70f1937806d04c6\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":178},{\"address\":\"0x740058839a1668af5700e5d7b062007275e77d25\",\"code_hash\":\"0x0a873a34bb834900b94eb95ae7f8ef4f8b911c11e7136a95f69b07937031905b\",\"code_size\":23506,\"count\":178},{\"address\":\"0xf9e037dcf792ba8c4a0ca570eac7cbcbafabd9d4\",\"code_hash\":\"0x389f088ae11bce54bc7af2f4491bae744cd611e2ef2defddd79d9c88070d7f09\",\"code_size\":4532,\"count\":177},{\"address\":\"0xdfaa75323fb721e5f29d43859390f62cc4b600b8\",\"code_hash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"code_size\":0,\"count\":177},{\"address\":\"0xef4461891dfb3ac8572ccf7c794664a8dd927945\",\"code_hash\":\"0xca6a00816e5670fed65db2bd57bd3d72aef67f2509d251dbf6147c22fd4ab8ff\",\"code_size\":1501,\"count\":177},{\"address\":\"0xd140a55ceb2a12a0ff22ca3cad62011ba0e3b780\",\"code_hash\":\"0x723023cad9b206aa0aa51c5d252e8c6e36611ea3643ff4e81e60d10a601b6140\",\"code_size\":14019,\"count\":177},{\"address\":\"0xf27d4fb3b1c194f94b9966cc75b4bbb686008c8c\",\"code_hash\":\"0xec108cae4094f8893ddafe39d7597973dfb402de13e5b5dba302eedc554dfa0b\",\"code_size\":13946,\"count\":177},{\"address\":\"0x1d13070b48823b68ca9dc9a55bfc0945bd7c59af\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":176},{\"address\":\"0x9d4d54e198e8fdad10d8bcf9594ef0384dc34e3d\",\"code_hash\":\"0x11da9b0ca81140c427fea8726dabeb8b0b25adfc78edd6eea68a1654a879f48e\",\"code_size\":1900,\"count\":176},{\"address\":\"0x77760222d2780199aa666c44cf935ec3d2e884a6\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":176},{\"address\":\"0x1e914daedf6b9410483ef402a3e2d51ee01273cb\",\"code_hash\":\"0x5b83bdbcc56b2e630f2807bbadd2b0c21619108066b92a58de081261089e9ce5\",\"code_size\":11293,\"count\":175},{\"address\":\"0x23878914efe38d27c4d67ab83ed1b93a74d4086a\",\"code_hash\":\"0x82c6d153799b3226525e3b7ec27b843ef44c5f6bca21fcf8b3c80db61ba64881\",\"code_size\":2400,\"count\":175},{\"address\":\"0x70bb8bcfafcf60446d5071d4427da8e99f0168aa\",\"code_hash\":\"0xf62fbdcd23a27dd7078f71009de9d62c888173a4f759eb30cd9681bdf31eaa44\",\"code_size\":7590,\"count\":175},{\"address\":\"0xbd3fa81b58ba92a82136038b25adec7066af3155\",\"code_hash\":\"0x519000e819bce41995ccfafc20054ce0a10164f3ac63298076a5efe416f7b484\",\"code_size\":13497,\"count\":175},{\"address\":\"0x6df1c1e379bc5a00a7b4c6e67a203333772f45a8\",\"code_hash\":\"0x82c6d153799b3226525e3b7ec27b843ef44c5f6bca21fcf8b3c80db61ba64881\",\"code_size\":2400,\"count\":173},{\"address\":\"0xad55aebc9b8c03fc43cd9f62260391c13c23e7c0\",\"code_hash\":\"0x5e4dcb0bb1910f6429e5fe91678990088a51c6d1cfe1b31d05fb9d948cc7867c\",\"code_size\":708,\"count\":172},{\"address\":\"0xc71b5f631354be6853efe9c3ab6b9590f8302e81\",\"code_hash\":\"0x394d4d376555782167cb33299c64af9458b48c4a98f99e15a627fb54de180372\",\"code_size\":5824,\"count\":172},{\"address\":\"0xb9413a34f68072a362b6a58f46c6af36c4e5d890\",\"code_hash\":\"0x9ce6011c0581e3985a88aa039796fb5ea6459e7a0ba5358cfde6436f192f0c1d\",\"code_size\":14222,\"count\":172}]}";

            var doc = JsonDocument.Parse(stats);

            var valuesElement = doc.RootElement.GetProperty("stats");

            foreach (JsonElement item in valuesElement.EnumerateArray())
            {
                if (item.TryGetProperty("address", out JsonElement addressElement) &&
                    item.TryGetProperty("code_size", out JsonElement sizeElement) &&
                    item.TryGetProperty("count", out JsonElement countElement))
                {
                    var codeSize = sizeElement.GetInt32();
                    var count = countElement.GetInt32();

                    if (codeSize == 0) continue;

                    if (count < 750) continue;

                    string address = addressElement.GetString();
                    yield return address;
                }
            }

            yield return "0xBB9bc244D798123fDe783fCc1C72d3Bb8C189413";
        }



        [Test]
        public async Task ILVM_PLAYGROUND()
        {
            string[] targets = GetTargetAddress().ToArray();

            String path = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedContractsTests");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            var config = new VMConfig
            {
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IlEvmContractsPerDllCount = Math.Min(targets.Length, 64),
                IsIlEvmAggressiveModeEnabled = true,
                IlEvmPersistPrecompiledContractsOnDisk = true,
                IlEvmPrecompiledContractsPath = path,
            };

            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(config, Prague.Instance);


            string fileName = Precompiler.GetTargetFileName();
            var assemblyPath = Path.Combine(path, fileName);

            foreach (var target in targets)
            {
                Address.TryParse(target, out Address targetAddress);

                var rpcUrl = "endpoint"; // or your own node
                var address = target;

                var requestBody = new
                {
                    jsonrpc = "2.0",
                    method = "eth_getCode",
                    @params = new[] { address, "latest" },
                    id = 1
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var httpClient = new HttpClient();
                var response = await httpClient.PostAsync(rpcUrl, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode is HttpStatusCode.OK)
                {
                    using var doc = JsonDocument.Parse(responseContent);
                    var code = doc.RootElement.GetProperty("result").GetString();

                    byte[] bytecode = Bytes.FromHexString(code);
                    var addressFromCode = enhancedChain.InsertCode(bytecode, targetAddress);

                    enhancedChain.ForceRunAnalysis(addressFromCode, ILMode.DYNAMIC_AOT_MODE);
                }
            }

            if (Precompiler._currentBundleSize > 0)
            {
                Precompiler.FlushToDisk(config);
            }
        }


        [Test]
        public void ILVM_AOT_WhiteList_Is_Handled()
        {
            var bytecode1 = Prepare.EvmCode
                .PushData(UInt256.MaxValue)
                .PushData(Address.SystemUser)
                .PushData(1)
                .STOP()
                .Done;

            var bytecode2 = Prepare.EvmCode
                .PushData(UInt256.MaxValue)
                .PushData(Address.SystemUser)
                .PushData(2)
                .STOP()
                .Done;

            var codeHash1 = Keccak.Compute(bytecode1);
            var codeHash2 = Keccak.Compute(bytecode2);

            AotContractsRepository.ReserveForWhitelisting(codeHash1);

            IlVirtualMachineTestsBase enhancedChain = new IlVirtualMachineTestsBase(new VMConfig
            {
                IlEvmEnabledMode = ILMode.DYNAMIC_AOT_MODE,
                IlEvmAnalysisThreshold = 1,
                IlEvmAnalysisQueueMaxSize = 1,
                IsIlEvmAggressiveModeEnabled = true,
                IlEvmPersistPrecompiledContractsOnDisk = false,
                IlEvmAllowedContracts = [codeHash1.ToString()],
            }, Prague.Instance);

            var address1 = enhancedChain.InsertCode(bytecode1, codeHash1);
            var address2 = enhancedChain.InsertCode(bytecode2, codeHash2);

            var isCode1Found = AotContractsRepository.TryGetIledCode(codeHash1, out var iledCodeBefore);
            var isCode1Whitelisted = AotContractsRepository.IsWhitelisted(codeHash1);
            Assert.That(iledCodeBefore, Is.Null, "reserved AOT code should not be generated before execution");
            Assert.That(isCode1Found, Is.False, "AOT code should not be generated before execution");
            Assert.That(isCode1Whitelisted, Is.True, "AOT code should be whitelisted");

            var isCode2Found = AotContractsRepository.TryGetIledCode(codeHash2, out var iledCode2Before);
            var isCode2Whitelisted = AotContractsRepository.IsWhitelisted(codeHash2);
            Assert.That(iledCode2Before, Is.Null, "AOT code should not be generated for non-whitelisted contract");
            Assert.That(isCode2Found, Is.False, "AOT code should not be generated for non-whitelisted contract");
            Assert.That(isCode2Whitelisted, Is.False, "AOT code should not be whitelisted for non-whitelisted contract");


            enhancedChain.Execute<ITxTracer>(bytecode1, NullTxTracer.Instance, forceAnalysis: false);
            enhancedChain.Execute<ITxTracer>(bytecode2, NullTxTracer.Instance, forceAnalysis: false);

            Thread.Sleep(TimeSpan.FromSeconds(5));

            enhancedChain.Execute<ITxTracer>(bytecode1, NullTxTracer.Instance, forceAnalysis: false);
            enhancedChain.Execute<ITxTracer>(bytecode2, NullTxTracer.Instance, forceAnalysis: false);

            var codeInfo1 = enhancedChain.GetCodeInfo(address1);
            AotContractsRepository.TryGetIledCode(codeHash1, out var iledCodeAfter);
            Assert.That(iledCodeAfter, Is.Not.Null, "AOT code should be generated for whitelisted contract");

            var code1phase = codeInfo1.IlInfo.AnalysisPhase;
            Assert.That(code1phase, Is.EqualTo(AnalysisPhase.Completed), "AOT code should be processed for whitelisted contract");


            var codeInfo2 = enhancedChain.GetCodeInfo(address2);
            AotContractsRepository.TryGetIledCode(codeHash2, out var iledCode2After);
            Assert.That(iledCode2After, Is.Null, "AOT code should not be generated for non-whitelisted contract");

            var code2phase = codeInfo2.IlInfo.AnalysisPhase;
            Assert.That(code2phase, Is.EqualTo(AnalysisPhase.NotStarted), "AOT code should not be processed for non-whitelisted contract");

            Assert.That(Metrics.IlvmAotPrecompiledCalls, Is.EqualTo(1), "AOT precompiled calls should be counted for whitelisted contract");
        }
    }
}
