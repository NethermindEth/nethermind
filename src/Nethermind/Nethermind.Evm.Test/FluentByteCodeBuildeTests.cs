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

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Evm;
using System.Runtime.ConstrainedExecution;
using Nethermind.Core.Test;
using FluentAssertions.Execution;
using System.Reflection;
using System.Linq;
using static Nethermind.HashLib.HashFactory.Crypto;
using System.Reflection.Emit;
using Nethermind.Int256;
using System.Reflection.Metadata;
using FastEnumUtility;
using System;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;

namespace Nethermind.Evm.Test
{
    public class FluentBytecodeBuilderTests : VirtualMachineTestsBase
    {
        public class TestCase
        {
            public byte[] FluentCodes;
            public byte[] ResultCodes;
            public string Description;
        }

        public static MethodInfo GetFluentOpcodeFunction(Instruction opcode)
        {
            // Source generating those functions in the BytecodeBuilderExtensions.cs might remove all this redundancy
            static bool HasDigit(Instruction opcode, Instruction[] treatLikeSuffexedOpcode, Instruction[] treatLikeNonSuffexedOpcode, out int prefixLen, out string opcodeAsString)
            {
                // opcode with multiple indexes at the end like PUSH or DUP or SWAP are represented as one function
                // with the char 'x' instead of the number with one byte argument to diff i.g : PUSH32 => PUSHx(32, ...)
                opcodeAsString = opcode.ToString();
                prefixLen = opcodeAsString.Length;

                // STORE8 is excluded from filter and always returns false cause it is one of it own and has a function mapped directly to it
                if (treatLikeSuffexedOpcode.Contains(opcode))
                {
                    return false;
                }

                // CREATE is included from filter and always return true it is mapped to CREATE(byte)
                if (treatLikeNonSuffexedOpcode.Contains(opcode))
                {
                    return true;
                }

                // we check if opcode has a digit at its end 
                bool hasDigits = false;
                int i = opcodeAsString.Length - 1;
                while (i > 0)
                {
                    if (char.IsLetter(opcodeAsString[i]))
                    {
                        break;
                    }
                    hasDigits = true;
                    i--;
                }

                // length of prefix (excluding suffix number if it exists)
                prefixLen = i + 1;
                return hasDigits;
            }
            bool hasDigit = HasDigit(opcode, new[] { Instruction.MSTORE8 }, new[] { Instruction.CREATE }, out int prefixLen, out string opcodeStr);
            return typeof(BytecodeBuilder).GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(method =>
                {
                    if(hasDigit)
                    {
                        return method.Name.StartsWith(opcodeStr.Substring(0, prefixLen));
                    } else
                    {
                        return method.Name == opcodeStr;
                    }
                })
                .FirstOrDefault();
        }

        public static IEnumerable<TestCase> opcodes_with_0_arg()
        {
            var opcodes = new Instruction[] {
                    Instruction.STOP,
                    Instruction.CALLER,
                    Instruction.CALLVALUE,
                    Instruction.ORIGIN,
                    Instruction.CALLDATASIZE,
                    Instruction.CODESIZE,
                    Instruction.GASPRICE,
                    Instruction.RETURNDATASIZE,
                    Instruction.COINBASE,
                    Instruction.PREVRANDAO,
                    Instruction.TIMESTAMP,
                    Instruction.NUMBER,
                    Instruction.GASLIMIT,
                    Instruction.CHAINID,
                    Instruction.SELFBALANCE,
                    Instruction.BASEFEE,
                    Instruction.POP,
                    Instruction.PC,
                    Instruction.JUMPDEST,
                    Instruction.MSIZE,
                    Instruction.BEGINSUB,
                    Instruction.RETURNSUB,
                    Instruction.INVALID,

                    Instruction.SWAP1, Instruction.SWAP5, Instruction.SWAP9 , Instruction.SWAP13,
                    Instruction.SWAP2, Instruction.SWAP6, Instruction.SWAP10, Instruction.SWAP14,
                    Instruction.SWAP3, Instruction.SWAP7, Instruction.SWAP11, Instruction.SWAP15,
                    Instruction.SWAP4, Instruction.SWAP8, Instruction.SWAP12, Instruction.SWAP16,

                    Instruction.DUP1, Instruction.DUP5, Instruction.DUP9 , Instruction.DUP13,
                    Instruction.DUP2, Instruction.DUP6, Instruction.DUP10, Instruction.DUP14,
                    Instruction.DUP3, Instruction.DUP7, Instruction.DUP11, Instruction.DUP15,
                    Instruction.DUP4, Instruction.DUP8, Instruction.DUP12, Instruction.DUP16,
                };
            foreach (var opcode in opcodes)
            {
                var initBytecode = Prepare.EvmCode;

                //we get MethodInfo of the function representing the opcode
                var method = GetFluentOpcodeFunction(opcode);

                //we handle the cases requiring a byte differentiator 
                initBytecode = opcode switch
                {
                    >= Instruction.SWAP1 and <= Instruction.SWAP16 => (Prepare)method.Invoke(null, new object[] { initBytecode, (byte)(opcode - Instruction.SWAP1 + 1) }),
                    >= Instruction.DUP1 and <= Instruction.DUP16 => (Prepare)method.Invoke(null, new object[] { initBytecode, (byte)(opcode - Instruction.DUP1 + 1) }),
                    _ => (Prepare)method.Invoke(null, new object[] { initBytecode })
                };

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 0 args",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .Op(opcode)
                        .Done
                };
            }
        }

        public static IEnumerable<TestCase> opcodes_with_1_arg()
        {
            // opcode having 1 argument of type Address
            var address_opcodes = new Instruction[] {
                    Instruction.BALANCE,
                    Instruction.EXTCODESIZE,
                    Instruction.EXTCODEHASH,
                    Instruction.SELFDESTRUCT,

            };

            // opcode having 1 argument of type UInt256
            var number_opcodes = new Instruction[] {
                    Instruction.ISZERO,
                    Instruction.NOT,
                    Instruction.CALLDATALOAD,
                    Instruction.BLOCKHASH,
                    Instruction.JUMP,
                    Instruction.SLOAD,
                    Instruction.TLOAD,
                    Instruction.MLOAD,
                    Instruction.JUMPSUB,
                };

            foreach(Instruction opcode in address_opcodes)
            {
                Address arguments = Address.Zero;
                var initBytecode = Prepare.EvmCode;
                MethodInfo method = GetFluentOpcodeFunction(opcode);
                initBytecode = (Prepare)method.Invoke(null, new object[] {
                    initBytecode , arguments
                });


                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 1 args ({arguments})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(arguments)
                        .Op(opcode)
                        .Done
                };
            }

            foreach (Instruction opcode in number_opcodes)
            {
                UInt256 arguments = UInt256.Zero;
                var initBytecode = Prepare.EvmCode;
                MethodInfo method = GetFluentOpcodeFunction(opcode);
                initBytecode = (Prepare)method.Invoke(null, new object[] {
                    initBytecode , arguments
                });

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 1 args ({arguments})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(arguments)
                        .Op(opcode)
                        .Done
                };
            }
        }

        public static IEnumerable<TestCase> opcodes_with_2_arg()
        {
            // opcode having 2 argument of diff type (UInt256, byte[])
            var heterogeneous_opcodes = new[] {
                        Instruction.SIGNEXTEND,
                        Instruction.MSTORE,
                        Instruction.BYTE,
                        Instruction.MSTORE8,
                        Instruction.TSTORE,
                        Instruction.SSTORE,
                        Instruction.JUMPI
            };

            // opcode having 2 argument of same type UInt256
            var homogeneous_opcodes = new[] {
                        Instruction.ADD,
                        Instruction.MUL,
                        Instruction.SUB,
                        Instruction.DIV,
                        Instruction.SDIV,
                        Instruction.MOD,
                        Instruction.SMOD,
                        Instruction.EXP,
                        Instruction.LT,
                        Instruction.SLT,
                        Instruction.GT,
                        Instruction.SGT,
                        Instruction.EQ,
                        Instruction.AND,
                        Instruction.OR,
                        Instruction.XOR,
                        Instruction.SHA3,
                        Instruction.SHL,
                        Instruction.SHR,
                        Instruction.SAR,
                        Instruction.SHA3,
                        Instruction.RETURN,
                        Instruction.REVERT,

                        Instruction.LOG1, Instruction.LOG2, Instruction.LOG3, Instruction.LOG4
                    };

            foreach (Instruction opcode in homogeneous_opcodes)
            {
                (UInt256 firstArg, UInt256 secondArg) = (UInt256.Zero, UInt256.One);
                var initBytecode = Prepare.EvmCode;
                var method = GetFluentOpcodeFunction(opcode);
                initBytecode = opcode switch
                {
                    >= Instruction.LOG1 and <= Instruction.LOG4 => (Prepare)method.Invoke(null, new object[] { initBytecode, (byte)(opcode - Instruction.LOG1 + 1), firstArg, secondArg }),
                    _ => (Prepare)method.Invoke(null, new object[] { initBytecode, firstArg, secondArg })
                };

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 2 args ({firstArg}, {secondArg})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(secondArg)
                        .PushData(firstArg)
                        .Op(opcode)
                        .Done
                };
            }

            foreach (Instruction opcode in heterogeneous_opcodes)
            {
                (UInt256 firstArg, byte[] secondArg) = (UInt256.Zero, new byte[] { 2 });
                var initBytecode = Prepare.EvmCode;
                var method = GetFluentOpcodeFunction(opcode);
                initBytecode = (Prepare)method.Invoke(null, new object[] { initBytecode, firstArg, secondArg });

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 2 args ({firstArg}, {secondArg.ToHexString(true)})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .Op(Instruction.PUSH1)
                        .Data(secondArg)
                        .PushData(firstArg)
                        .Op(opcode)
                        .Done
                };
            }
        }

        public static IEnumerable<TestCase> opcodes_with_3_arg()
        {
            var opcodes = new Instruction[] {
                    Instruction.ADDMOD,
                    Instruction.MULMOD,
                    Instruction.CODECOPY,
                    Instruction.CALLDATACOPY,
                    Instruction.RETURNDATACOPY,

                    Instruction.CREATE, Instruction.CREATE2
            };
            foreach (var opcode in opcodes)
            {
                (UInt256 arg1, UInt256 arg2, UInt256 arg3) = (UInt256.Zero, UInt256.One, 23);
                var initBytecode = Prepare.EvmCode;
                var method = GetFluentOpcodeFunction(opcode);
                initBytecode = opcode switch
                {
                    Instruction.CREATE or Instruction.CREATE2 => (Prepare)method.Invoke(null, new object[] { initBytecode, (byte)(opcode == Instruction.CREATE2 ? 2 : 0), arg1, arg2, arg3 }),
                    _ => (Prepare)method.Invoke(null, new object[] { initBytecode, arg1, arg2, arg3 })
                };

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with 3 args ({arg1}, {arg2}, {arg3})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(arg3)
                        .PushData(arg2)
                        .PushData(arg1)
                        .Op(opcode)
                        .Done
                };
            }
        }

        public static IEnumerable<TestCase> FluentBuilderTestCases
        {
            get
            {
                foreach (var testCase in opcodes_with_0_arg())
                {
                    yield return testCase;
                }

                foreach (var testCase in opcodes_with_1_arg())
                {
                    yield return testCase;
                }

                foreach (var testCase in opcodes_with_2_arg())
                {
                    yield return testCase;
                }

                foreach (var testCase in opcodes_with_3_arg())
                {
                    yield return testCase;
                }

                yield return new TestCase()
                {
                    Description = "Test : ADD-OP on empty stack",
                    FluentCodes = Prepare.EvmCode
                        .ADD(3, 2)
                        .Done,
                    ResultCodes = Prepare.EvmCode
                        .Op(Instruction.PUSH1)
                        .Data(2)
                        .Op(Instruction.PUSH1)
                        .Data(3)
                        .Op(Instruction.ADD)
                        .Done
                };

                yield return new TestCase()
                {
                    Description = "Test : SUB-OP on filled stack",
                    FluentCodes = Prepare.EvmCode
                        .ADD(2, 3)
                        .SUB(1)
                        .Done,
                    ResultCodes = Prepare.EvmCode
                        .Op(Instruction.PUSH1)
                        .Data(3)
                        .Op(Instruction.PUSH1)
                        .Data(2)
                        .Op(Instruction.ADD)
                        .Op(Instruction.PUSH1)
                        .Data(1)
                        .Op(Instruction.SUB)
                        .Done
                };

                yield return new TestCase()
                {
                    Description = "Test : Complex test case",
                    FluentCodes = Prepare.EvmCode
                        .SLOAD(1)
                        .EQ(1)
                        .JUMPI(17)
                        .SSTORE(1, new byte[] { 1 })
                        .JUMP(40)
                        .JUMPDEST()
                        .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
                        .JUMPDEST()
                        .Done,

                    ResultCodes = Prepare.EvmCode
                        .PushData(1)
                        .Op(Instruction.SLOAD)
                        .PushData(1)
                        .Op(Instruction.EQ)
                        .PushData(17)
                        .Op(Instruction.JUMPI)
                        .PushData(1)
                        .PushData(1)
                        .Op(Instruction.SSTORE)
                        .PushData(40)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(TestItem.PrivateKeyB.Address)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done
                };
            }
        }

        [Test]
        public void code_emited_by_fluent_is_same_as_expected([ValueSource(nameof(FluentBuilderTestCases))] TestCase test)
        {
            test.FluentCodes.Should().BeEquivalentTo(test.ResultCodes, test.Description);
        }
    }
}
