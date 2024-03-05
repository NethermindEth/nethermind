// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class InvalidOpcodeTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ConstantinopleFixBlockNumber;

        private static readonly Instruction[] FrontierInstructions =
        {
            Instruction.STOP, Instruction.ADD, Instruction.MUL, Instruction.SUB, Instruction.DIV, Instruction.SDIV,
            Instruction.MOD, Instruction.SMOD, Instruction.ADDMOD, Instruction.MULMOD, Instruction.EXP,
            Instruction.SIGNEXTEND, Instruction.LT, Instruction.GT, Instruction.SLT, Instruction.SGT,
            Instruction.EQ, Instruction.ISZERO, Instruction.AND, Instruction.OR, Instruction.XOR, Instruction.NOT,
            Instruction.BYTE, Instruction.KECCAK256, Instruction.ADDRESS, Instruction.BALANCE, Instruction.ORIGIN,
            Instruction.CALLER, Instruction.CALLVALUE, Instruction.CALLDATALOAD, Instruction.CALLDATASIZE,
            Instruction.CALLDATACOPY, Instruction.CODESIZE, Instruction.CODECOPY, Instruction.GASPRICE,
            Instruction.EXTCODESIZE, Instruction.EXTCODECOPY, Instruction.BLOCKHASH, Instruction.COINBASE,
            Instruction.TIMESTAMP, Instruction.NUMBER, Instruction.PREVRANDAO, Instruction.GASLIMIT,
            Instruction.POP, Instruction.MLOAD, Instruction.MSTORE, Instruction.MSTORE8, Instruction.SLOAD,
            Instruction.SSTORE, Instruction.JUMP, Instruction.JUMPI, Instruction.PC, Instruction.MSIZE,
            Instruction.GAS, Instruction.JUMPDEST, Instruction.PUSH1, Instruction.PUSH2, Instruction.PUSH3,
            Instruction.PUSH4, Instruction.PUSH5, Instruction.PUSH6, Instruction.PUSH7, Instruction.PUSH8,
            Instruction.PUSH9, Instruction.PUSH10, Instruction.PUSH11, Instruction.PUSH12, Instruction.PUSH13,
            Instruction.PUSH14, Instruction.PUSH15, Instruction.PUSH16, Instruction.PUSH17, Instruction.PUSH18,
            Instruction.PUSH19, Instruction.PUSH20, Instruction.PUSH21, Instruction.PUSH22, Instruction.PUSH23,
            Instruction.PUSH24, Instruction.PUSH25, Instruction.PUSH26, Instruction.PUSH27, Instruction.PUSH28,
            Instruction.PUSH29, Instruction.PUSH30, Instruction.PUSH31, Instruction.PUSH32, Instruction.DUP1,
            Instruction.DUP2, Instruction.DUP3, Instruction.DUP4, Instruction.DUP5, Instruction.DUP6,
            Instruction.DUP7, Instruction.DUP8, Instruction.DUP9, Instruction.DUP10, Instruction.DUP11,
            Instruction.DUP12, Instruction.DUP13, Instruction.DUP14, Instruction.DUP15, Instruction.DUP16,
            Instruction.SWAP1, Instruction.SWAP2, Instruction.SWAP3, Instruction.SWAP4, Instruction.SWAP5,
            Instruction.SWAP6, Instruction.SWAP7, Instruction.SWAP8, Instruction.SWAP9, Instruction.SWAP10,
            Instruction.SWAP11, Instruction.SWAP12, Instruction.SWAP13, Instruction.SWAP14, Instruction.SWAP15,
            Instruction.SWAP16, Instruction.LOG0, Instruction.LOG1, Instruction.LOG2, Instruction.LOG3,
            Instruction.LOG4, Instruction.CREATE, Instruction.CALL, Instruction.CALLCODE, Instruction.RETURN,
            Instruction.SELFDESTRUCT
        };

        private static readonly Instruction[] HomesteadInstructions =
            FrontierInstructions.Union(
                new[] { Instruction.DELEGATECALL }).ToArray();

        private static readonly Instruction[] ByzantiumInstructions =
            HomesteadInstructions.Union(
                new[]
                {
                    Instruction.REVERT, Instruction.STATICCALL, Instruction.RETURNDATACOPY,
                    Instruction.RETURNDATASIZE
                }).ToArray();

        private static readonly Instruction[] ConstantinopleFixInstructions =
            ByzantiumInstructions.Union(
                new[]
                {
                    Instruction.CREATE2, Instruction.EXTCODEHASH, Instruction.SHL, Instruction.SHR,
                    Instruction.SAR
                }).ToArray();

        private static readonly Instruction[] IstanbulInstructions =
            ConstantinopleFixInstructions.Union(
                new[] { Instruction.SELFBALANCE, Instruction.CHAINID }).ToArray();

        private static readonly Instruction[] BerlinInstructions =
            IstanbulInstructions.Union(
                // new[]
                // {
                //     Instruction.BEGINSUB,
                //     Instruction.JUMPSUB,
                //     Instruction.RETURNSUB
                // }
                new Instruction[] { }
            ).ToArray();

        private static readonly Instruction[] LondonInstructions =
            BerlinInstructions.Union(
                new Instruction[]
                {
                    Instruction.BASEFEE
                }
            ).ToArray();

        private static readonly Instruction[] ShanghaiInstructions =
            LondonInstructions.Union(
                new Instruction[]
                {
                    Instruction.PUSH0
                }
            ).ToArray();

        private static readonly Instruction[] CancunInstructions =
            ShanghaiInstructions.Union(
                new Instruction[]
                {
                    Instruction.TSTORE,
                    Instruction.TLOAD,
                    Instruction.MCOPY,
                    Instruction.BLOBHASH,
                    Instruction.BLOBBASEFEE
                }
            ).ToArray();

        private static readonly Instruction[] PragueInstructions =
            CancunInstructions.Union(
                new Instruction[]
                {
                    Instruction.RJUMP,
                    Instruction.RJUMPI,
                    Instruction.RJUMPV,
                    Instruction.CALLF,
                    Instruction.RETF,
                    Instruction.JUMPF,
                    Instruction.EOFCREATE,
                    Instruction.TXCREATE,
                    Instruction.RETURNCONTRACT,
                    Instruction.DATASIZE,
                    Instruction.DATACOPY,
                    Instruction.DATALOAD,
                    Instruction.DATALOADN,
                    Instruction.SWAPN,
                    Instruction.DUPN,
                    Instruction.EXCHANGE,
                    Instruction.CALL2,
                    Instruction.DELEGATECALL2,
                    Instruction.STATICCALL2,
                }
            ).ToArray();

        private readonly Dictionary<ForkActivation, Instruction[]> _validOpcodes
            = new()
            {
                {(ForkActivation)0, FrontierInstructions},
                {(ForkActivation)MainnetSpecProvider.HomesteadBlockNumber, HomesteadInstructions},
                {(ForkActivation)MainnetSpecProvider.SpuriousDragonBlockNumber, HomesteadInstructions},
                {(ForkActivation)MainnetSpecProvider.TangerineWhistleBlockNumber, HomesteadInstructions},
                {(ForkActivation)MainnetSpecProvider.ByzantiumBlockNumber, ByzantiumInstructions},
                {(ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber, ConstantinopleFixInstructions},
                {(ForkActivation)MainnetSpecProvider.IstanbulBlockNumber, IstanbulInstructions},
                {(ForkActivation)MainnetSpecProvider.MuirGlacierBlockNumber, IstanbulInstructions},
                {(ForkActivation)MainnetSpecProvider.BerlinBlockNumber, BerlinInstructions},
                {(ForkActivation)MainnetSpecProvider.LondonBlockNumber, LondonInstructions},
                {MainnetSpecProvider.ShanghaiActivation, ShanghaiInstructions},
                {MainnetSpecProvider.CancunActivation, CancunInstructions},
                {MainnetSpecProvider.PragueActivation, PragueInstructions},
                {(long.MaxValue, ulong.MaxValue), PragueInstructions}
            };

        private const string InvalidOpCodeErrorMessage = "BadInstruction";

        private ILogManager _logManager = LimboLogs.Instance;

        protected override ILogManager GetLogManager()
        {
            _logManager ??= new OneLoggerLogManager(new(new NUnitLogger(LogLevel.Trace)));
            return _logManager;
        }

        [TestCase(0)]
        [TestCase(MainnetSpecProvider.HomesteadBlockNumber)]
        [TestCase(MainnetSpecProvider.SpuriousDragonBlockNumber)]
        [TestCase(MainnetSpecProvider.TangerineWhistleBlockNumber)]
        [TestCase(MainnetSpecProvider.ByzantiumBlockNumber)]
        [TestCase(MainnetSpecProvider.IstanbulBlockNumber)]
        [TestCase(MainnetSpecProvider.ConstantinopleFixBlockNumber)]
        [TestCase(MainnetSpecProvider.MuirGlacierBlockNumber)]
        [TestCase(MainnetSpecProvider.BerlinBlockNumber)]
        [TestCase(MainnetSpecProvider.LondonBlockNumber)]
        [TestCase(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp)]
        [TestCase(MainnetSpecProvider.ParisBlockNumber + 2, MainnetSpecProvider.CancunBlockTimestamp)]
        [TestCase(long.MaxValue, ulong.MaxValue)]
        public void Test(long blockNumber, ulong? timestamp = null)
        {
            ILogger logger = _logManager.GetClassLogger();
            Instruction[] validOpcodes = _validOpcodes[(blockNumber, timestamp)];
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                bool isEofContext = timestamp >= MainnetSpecProvider.PragueActivation.Timestamp;
                bool isValidOpcode = false;

                Instruction opcode = (Instruction)i;

                if (opcode == Instruction.RETF) continue;

                byte[] code = Prepare.EvmCode
                        .Op((byte)i)
                        .Done; ;

                if(InstructionExtensions.IsValid(opcode, true) && !InstructionExtensions.IsValid(opcode, false))
                {
                    var opcodeMetadata = InstructionExtensions.StackRequirements(opcode);
                    opcodeMetadata.InputCount ??= 1;
                    opcodeMetadata.OutputCount ??= (opcode is Instruction.DUPN ? (ushort)2 : (ushort)1);

                    bool isFunCall = opcode is Instruction.CALLF;

                    byte[] stackHeighExpected = BitConverter.GetBytes(Math.Max(opcodeMetadata.InputCount.Value, opcodeMetadata.OutputCount.Value));

                    List<byte> codesection = new();

                    for(var j = 0; j < opcodeMetadata.InputCount; j++)
                    {
                        codesection.AddRange(
                            Prepare.EvmCode
                                .PushSingle(0)
                                .Done
                        );
                    }

                    codesection.Add((byte)i);

                    for (var j = 0; j < (opcodeMetadata.immediates ?? 3); j++)
                    {
                        if(isFunCall && j == 1)
                        {
                            codesection.Add(1);
                            continue;
                        }
                        codesection.Add(0);
                    }

                    for (var j = 0; j < opcodeMetadata.OutputCount; j++)
                    {
                        codesection.AddRange(
                            Prepare.EvmCode
                                .Op(Instruction.POP)
                                .Done
                        );
                    }

                    if(opcode is not Instruction.JUMPF)
                    {
                        codesection.Add((byte)Instruction.STOP);
                    }


                    byte[] codeSectionSize = BitConverter.GetBytes((ushort)(codesection.Count));
                    code = [
                        // start header
                        0xef, 0x00,
                        0x01,
                        0x01,
                            0x00, (isFunCall ? (byte)0x08 : (byte)0x04),
                        0x02,
                            0x00, (isFunCall ? (byte)0x02 : (byte)0x01),
                            codeSectionSize[1], codeSectionSize[0],
                            .. (isFunCall ? [0x00, 0x01] : Array.Empty<byte>()),
                        0x03,
                            0x00, 0x01,
                            0x00, 0x02,
                        0x04,
                            0x00, 0x20,
                        0x00,
                        // end header
                        // start typesection
                            0x00, 0x80,
                            stackHeighExpected[1], stackHeighExpected[0],
                            .. (isFunCall ? [0x00, 0x00, 0x00, 0x00] : Array.Empty<byte>()),
                        // end typesection
                        // start codesection
                            // start codesection 0
                            .. codesection,
                            // end codesection 0
                            // start codesection 1
                            .. (isFunCall ? [(byte)Instruction.RETF]: Array.Empty<byte>()),
                            // end codesection 1
                        // end codesection
                        // start container section
                        (byte)Instruction.RETURNCONTRACT, 0x00,
                        // end container section
                        // start data section
                        .. Enumerable.Range(0, 32).Select(b => (byte)b).ToArray()
                        // end data section
                    ];
                }

                logger.Info($"============ Testing opcode {i}==================");
                isValidOpcode = (opcode != Instruction.INVALID) && validOpcodes.Contains((Instruction)i);
                TestAllTracerWithOutput result = Execute((blockNumber, timestamp ?? 0), 1_000_000, code);
                if (isValidOpcode)
                {
                    result.Error.Should().NotBe(InvalidOpCodeErrorMessage, ((Instruction)i).ToString());
                }
                else
                {
                    result.Error.Should().Be(InvalidOpCodeErrorMessage, ((Instruction)i).ToString());
                    result.StatusCode.Should().Be(0, ((Instruction)i).ToString());
                }
            }
        }
    }
}
