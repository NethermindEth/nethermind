// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using System.Reflection.Emit;
using Nethermind.Int256;
using System.Reflection.Metadata;
using FastEnumUtility;
using System;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test
{
    public class BytecodeBuilderExtensionsTests : VirtualMachineTestsBase
    {
        static IReleaseSpec _releaseSpec = Cancun.Instance;
        public class TestCase
        {
            public byte[] FluentCodes;
            public byte[] ResultCodes;
            public string Description;
        }

        public static MethodInfo GetFluentOpcodeFunction(Instruction opcode)
        {
            static bool HasDigit(Instruction opcode, Instruction[] treatLikeSuffexedOpcode, Instruction[] treatLikeNonSuffexedOpcode, out int prefixLen, out string opcodeAsString)
            {
                // opcode with multiple indexes at the end like PUSH or DUP or SWAP are represented as one function
                // with the char 'x' instead of the number with one byte argument to diff i.g : PUSH32 => PUSHx(32, ...)
                opcodeAsString = opcode.GetName(true, _releaseSpec);
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
                    if (hasDigit)
                    {
                        return method.Name.StartsWith(opcodeStr.Substring(0, prefixLen));
                    }
                    else
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
                    Instruction.BEGINSUB => Prepare.EvmCode.BEGINSUB(),
                    Instruction.RETURNSUB => Prepare.EvmCode.RETURNSUB(),
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
                };

            foreach (Instruction opcode in address_opcodes)
            {
                if (_releaseSpec is Shanghai && opcode is Instruction.JUMPSUB)
                {
                    continue;
                }

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
                if (_releaseSpec is Shanghai && opcode is Instruction.CALLF)
                {
                    continue;
                }

                UInt256 arguments = UInt256.Zero;
                var initBytecode = Prepare.EvmCode;
                MethodInfo method = GetFluentOpcodeFunction(opcode);
                initBytecode = opcode switch
                {
                    Instruction.JUMPSUB => Prepare.EvmCode.JUMPSUB(),
                    _ => (Prepare)method.Invoke(null, new object[] {
                        initBytecode , arguments
                    })
                };

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

        public static IEnumerable<TestCase> opcodes_with_3_plus_arg()
        {
            var opcodeswith_7_args = new Instruction[] {
                    Instruction.CALL,
                    Instruction.CALLCODE,

            };
            var opcodeswith_6_args = new Instruction[] {
                    Instruction.STATICCALL,
                    Instruction.DELEGATECALL,
            };

            (UInt256 gasLim, Address codeSrc, UInt256 callValue, UInt256 dataOffset, UInt256 dataLength, UInt256 outputOffset, UInt256 outputLength) = (UInt256.One, Address.Zero, UInt256.One, UInt256.Zero, UInt256.One, UInt256.Zero, UInt256.One);
            foreach (var opcode in opcodeswith_7_args)
            {

                var initBytecode = Prepare.EvmCode;
                var method = GetFluentOpcodeFunction(opcode);
                var args = new object[] { initBytecode, gasLim, codeSrc, callValue, dataOffset, dataLength, outputOffset, outputLength };

                initBytecode = (Prepare)method.Invoke(null, args);

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with {args.Length} args ({args.Aggregate((acc, v) => $"{acc},{v}")})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(outputLength)
                        .PushData(outputOffset)
                        .PushData(dataLength)
                        .PushData(dataOffset)
                        .PushData(callValue)
                        .PushData(codeSrc)
                        .PushData(gasLim)
                        .Op(opcode)
                        .Done
                };
            }

            foreach (var opcode in opcodeswith_6_args)
            {

                var initBytecode = Prepare.EvmCode;
                var method = GetFluentOpcodeFunction(opcode);
                var args = new object[] { initBytecode, gasLim, codeSrc, dataOffset, dataLength, outputOffset, outputLength };

                initBytecode = (Prepare)method.Invoke(null, args);

                yield return new TestCase()
                {
                    Description = $"Testing opcode {opcode} : with {args.Length} args ({args.Aggregate((acc, v) => $"{acc},{v}")})",
                    FluentCodes = initBytecode.Done,
                    ResultCodes = Prepare.EvmCode
                        .PushData(outputLength)
                        .PushData(outputOffset)
                        .PushData(dataLength)
                        .PushData(dataOffset)
                        .PushData(codeSrc)
                        .PushData(gasLim)
                        .Op(opcode)
                        .Done
                };
            }

            yield return new TestCase()
            {
                Description = $"Testing opcode {Instruction.EXTCODECOPY} : with 4 args ({Address.Zero}, {UInt256.One}, {UInt256.Zero}, {UInt256.One})",
                FluentCodes = Prepare.EvmCode
                    .EXTCODECOPY(Address.Zero, UInt256.One, UInt256.Zero, UInt256.One)
                    .Done,
                ResultCodes = Prepare.EvmCode
                    .PushData(UInt256.One)
                    .PushData(UInt256.Zero)
                    .PushData(UInt256.One)
                    .PushData(Address.Zero)
                    .Op(Instruction.EXTCODECOPY)
                    .Done
            };
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

                yield return new TestCase()
                {
                    Description = @"Test :
                                    var n = 23;
                                    var i = 2;
                                    var r = 0
                                    while(i * i < n) {
                                        r = n % i == 0;
                                        i+=1
                                    }
                                    return r",
                    FluentCodes = Prepare.EvmCode
                        .COMMENT("Store variable(n) in Memory")
                        .MSTORE(0, new byte[] { 23 })
                        .COMMENT("Store Indexer(i) in Memory")
                        .MSTORE(32, new byte[] { 2 })
                        .COMMENT("Store Result(r) in Memory")
                        .MSTORE(64, new byte[] { 0 })
                        .COMMENT("We mark this place as a GOTO section")
                        .JUMPDEST()
                        .COMMENT("We check if i * i < n")
                        .MLOAD(32)
                        .MLOAD(32)
                        .MUL()
                        .MLOAD(0)
                        .LT()
                        .JUMPI(53)
                        .COMMENT("We check if n % i == 0")
                        .MLOAD(32)
                        .MLOAD(0)
                        .MOD()
                        .ISZERO()
                        .COMMENT("store n % i == 0 in Result(r)")
                        .MSTORE(64)
                        .COMMENT("increment Indexer(i)")
                        .MLOAD(32)
                        .ADD(1)
                        .MSTORE(32)
                        .COMMENT("Loop back to top of conditional loop")
                        .JUMP(15)
                        .COMMENT("return Result(r)")
                        .JUMPDEST()
                        .RETURN(64, 32)
                        .Done,

                    ResultCodes = Prepare.EvmCode
                        .Op(Instruction.PUSH1)
                        .Data(23)
                        .Op(Instruction.PUSH1)
                        .Data(0)
                        .Op(Instruction.MSTORE)
                        .Op(Instruction.PUSH1)
                        .Data(2)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MSTORE)
                        .Op(Instruction.PUSH1)
                        .Data(0)
                        .Op(Instruction.PUSH1)
                        .Data(64)
                        .Op(Instruction.MSTORE)
                        .Op(Instruction.JUMPDEST)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.MUL)
                        .Op(Instruction.PUSH1)
                        .Data(0)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.LT)
                        .Op(Instruction.PUSH1)
                        .Data(53)
                        .Op(Instruction.JUMPI)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.PUSH1)
                        .Data(0)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.MOD)
                        .Op(Instruction.ISZERO)
                        .Op(Instruction.PUSH1)
                        .Data(64)
                        .Op(Instruction.MSTORE)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MLOAD)
                        .Op(Instruction.PUSH1)
                        .Data(1)
                        .Op(Instruction.ADD)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.MSTORE)
                        .Op(Instruction.PUSH1)
                        .Data(15)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .Op(Instruction.PUSH1)
                        .Data(32)
                        .Op(Instruction.PUSH1)
                        .Data(64)
                        .Op(Instruction.RETURN)
                        .Done
                };
            }
        }

        [Test]
        public void code_emited_by_fluent_is_same_as_expected([ValueSource(nameof(FluentBuilderTestCases))] TestCase test)
        {
            test.FluentCodes.Should().BeEquivalentTo(test.ResultCodes, test.Description);
        }


        public static IEnumerable<string> Opcodes => Enum.GetNames(typeof(Instruction));

        [Test]
        public void all_opcode_have_fluent_method([ValueSource(nameof(Opcodes))] string opcode)
        {
            string[] methods = typeof(BytecodeBuilder).GetMethods(BindingFlags.Static | BindingFlags.Public).Select(m => m.Name).ToArray();
            if (opcode is "CREATE2" or "CREATE") Assert.True(methods.Contains("CREATE"));
            if (opcode.StartsWith("SWAP")) Assert.True(methods.Contains("SWAP"));
            if (opcode.StartsWith("DUP")) Assert.True(methods.Contains("DUP"));
            if (opcode.StartsWith("PUSH")) Assert.True(methods.Contains("PUSH"));
            else Assert.True(methods.Contains(opcode.ToUpper()));
        }
    }
}
