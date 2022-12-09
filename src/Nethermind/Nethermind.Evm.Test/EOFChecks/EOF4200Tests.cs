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
using Nethermind.Specs.Forks;
using NSubstitute;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Blockchain;
using System;
using static Nethermind.Evm.CodeAnalysis.ByteCodeValidator;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Test;
using System.Text.Json;
using TestCase = Nethermind.Evm.Test.EofTestsBase.TestCase;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EOF4200Tests
    {
        private EofTestsBase Instance => EofTestsBase.Instance(SpecProvider);
        protected ISpecProvider SpecProvider => new TestSpecProvider(Frontier.Instance, new OverridableReleaseSpec(Shanghai.Instance)
        {
            IsEip4200Enabled = true,
            IsEip4750Enabled = false
        });

        // valid code
        [TestCase("0xEF00010100060060005DFFFB00", true, Description = "valid rjumpi with : offset = -5")]
        [TestCase("0xEF00010100090060005D000300000000", true, Description = "valid rjumpi with : offset = 3")]
        [TestCase("0xEF00010100060060005D000000", true, Description = "valid rjumpi with : offset = 0")]
        [TestCase("0xEF0001010004005C000000", true, Description = "valid rjump with : offset = 0")]
        [TestCase("0xEF0001010007005C000300000000", true, Description = "valid rjump with : offset = 3")]
        [TestCase("0xEF000101000500005CFFFC00", true, Description = "valid rjump with : offset = -4")]
        // code with invalid magic
        [TestCase("0xEF0001010001005C", false, Description = "rjump truncated")]
        [TestCase("0xEF0001010002005C00", false, Description = "rjump truncated")]
        [TestCase("0xEF00010100030060005D", false, Description = "rjumpi truncated")]
        [TestCase("0xEF00010100040060005D00", false, Description = "rjumpi truncated")]
        [TestCase("0xEF0001010004005CFFFB00", false, Description = "rjump invalid destination, offset :  -5")]
        [TestCase("0xEF0001010004005CFFF300", false, Description = "rjump invalid destination, offset : -13")]
        [TestCase("0xEF0001010004005C000200", false, Description = "rjump invalid destination, offset :   2")]
        [TestCase("0xEF0001010004005C000100", false, Description = "rjump invalid destination, offset :   1")]
        [TestCase("0xEF0001010004005CFFFF00", false, Description = "rjump invalid destination, offset :  -1")]
        [TestCase("0xEF00010100060060005CFFFC00", false, Description = "rjump invalid destination, offset :  4")]
        [TestCase("0xEF00010100060060005DFFF900", false, Description = "rjumpi invalid destination, offset :  -7")]
        [TestCase("0xEF00010100060060005DFFF100", false, Description = "rjumpi invalid destination, offset :  -15")]
        [TestCase("0xEF00010100060060005D000200", false, Description = "rjumpi invalid destination, offset :   2")]
        [TestCase("0xEF00010100060060005D000100", false, Description = "rjumpi invalid destination, offset :   1")]
        [TestCase("0xEF00010100060060005DFFFF00", false, Description = "rjumpi invalid destination, offset :   -1")]
        [TestCase("0xEF00010100060060005DFFFC00", false, Description = "rjumpi invalid destination, offset :   -4")]
        public void EIP4200_Compliant_formats_Test(string code, bool isCorrectlyFormated)
        {
            var bytecode = Prepare.EvmCode
                .FromCode(code)
                .Done;

            var TargetReleaseSpec = new OverridableReleaseSpec(Shanghai.Instance)
            {
                IsEip4750Enabled = false
            };


            bool checkResult = ByteCodeValidator.Instance.ValidateEofStructure(bytecode, TargetReleaseSpec, out _);

            checkResult.Should().Be(isCorrectlyFormated);
        }

        public static IEnumerable<TestCase> Eip4200TestCases
        {
            get
            {
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMP(1)
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPI(1, new byte[] { 1 })
                                .INVALID()
                                .JUMPDEST()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMP(11)
                                .INVALID()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMP(-13)
                                .STOP()
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMP(0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPI(10, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMP(11)
                                .COINBASE()
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .RJUMPI(-16, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 2 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 0 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPI(0, new byte[] { 1 })
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 2, 4 }, 1)
                                .INVALID()
                                .INVALID()
                                .PushData(2)
                                .PushData(3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 1, 6 }, 0)
                                .INVALID()
                                .ADD(2, 3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 0, 5 }, 4)
                                .ADD(2, 3)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Success, null),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Failure, "truncated jumptable"),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .MSTORE8(0, new byte[] { 1 })
                                .RETURN(0, 1)
                                .Done,
                    ResultIfEOF = (StatusCode.Failure, "jump to push immediate"),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 5 }, 0)
                                .Done,
                    ResultIfEOF = (StatusCode.Failure, "jumpv destination outside of bounds"),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };

                yield return new TestCase
                {
                    Code = Prepare.EvmCode
                                .RJUMPV(new short[] { 1 }, 0)
                                .Done,
                    ResultIfEOF = (StatusCode.Failure, "jumpv cant be last instruction"),
                    ResultIfNotEOF = (StatusCode.Failure, "Invalid opcode"),
                };
            }
        }

        [Test]
        public void RelativeStaticJumps_execution_tests([ValueSource(nameof(Eip4200TestCases))] TestCase testcase)
        {
            var bytecode = testcase.GenerateCode(isEof: true);
            TestAllTracerWithOutput receipts = Instance.EOF_contract_execution_tests(bytecode);
            receipts.StatusCode.Should().Be(testcase.ResultIfEOF.Status, $"{testcase.Description} failed with error : {receipts.Error}, bytecode : {bytecode.ToHexString()}");
        }
    }
}
