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

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class EofTestsBase : VirtualMachineTestsBase
    {
        public static EofTestsBase Instance(ISpecProvider provider)
        {
            var instance = new EofTestsBase();
            instance.SpecProvider = provider;
            instance.Setup();
            return instance;
        }
        public TestAllTracerWithOutput EOF_contract_execution_tests(byte[] testcase)
        {
            return Execute(BlockNumber, Timestamp, testcase);
        }
        public void EOF_contract_deployment_tests(TestCase testcase, IReleaseSpec spec)
        {
            TestState.CreateAccount(TestItem.AddressC, 200.Ether());
            byte[] createContract = testcase.Code;

            ILogManager logManager = GetLogManager();
            var customSpecProvider = new TestSpecProvider(Frontier.Instance, spec);
            Machine = new VirtualMachine(TestBlockhashProvider.Instance, customSpecProvider, logManager);
            _processor = new TransactionProcessor(customSpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, createContract);


            transaction.GasPrice = 20.GWei();
            transaction.To = null;
            transaction.Data = createContract;
            TestAllTracerWithOutput tracer = CreateTracer();

            _processor.Execute(transaction, block.Header, tracer);

            Assert.AreEqual(testcase.ResultIfEOF.Status == StatusCode.Failure, tracer.ReportedActionErrors.Any(x => x != EvmExceptionType.InvalidCode), $"{testcase.Description}\nFailed with error {tracer.Error} \ncode : {testcase.Code.ToHexString(true)}");
        }

        public class TestCase
        {
            public int Index;
            public byte[] Code;
            public byte[] Data;
            public (byte Status, string error) ResultIfEOF;
            public (byte Status, string error) ResultIfNotEOF;
            public string Description;
            public static byte[] Classicalcode(byte[] bytecode, byte[] data = null)
            {
                var bytes = new byte[(data is not null && data.Length > 0 ? data.Length : 0) + bytecode.Length];

                Array.Copy(bytecode, 0, bytes, 0, bytecode.Length);
                if (data is not null && data.Length > 0)
                {
                    Array.Copy(data, 0, bytes, bytecode.Length, data.Length);
                }

                return bytes;
            }

            public static byte[] EofBytecode(byte[] bytecode, byte[] data = null)
            {
                var bytes = new byte[(data is not null && data.Length > 0 ? 10 + data.Length : 7) + bytecode.Length];

                int i = 0;

                // set magic
                bytes[i++] = 0xEF; bytes[i++] = 0x00; bytes[i++] = 0x01;

                // set code section
                var lenBytes = bytecode.Length.ToByteArray();
                bytes[i++] = 0x01; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];

                // set PushData section
                if (data is not null && data.Length > 0)
                {
                    lenBytes = data.Length.ToByteArray();
                    bytes[i++] = 0x02; bytes[i++] = lenBytes[^2]; bytes[i++] = lenBytes[^1];
                }
                bytes[i++] = 0x00;

                // set the terminator byte
                Array.Copy(bytecode, 0, bytes, i, bytecode.Length);
                if (data is not null && data.Length > 0)
                {
                    Array.Copy(data, 0, bytes, i + bytecode.Length, data.Length);
                }

                return bytes;
            }
            public byte[] GenerateCode(bool isEof)
            {
                return isEof ? EofBytecode(Code, Data) : Classicalcode(Code, Data);
            }
        }

        public static IEnumerable<IReleaseSpec> Specs
        {
            get
            {
                yield return GrayGlacier.Instance;
                yield return Shanghai.Instance;
            }
        }

    }
}
