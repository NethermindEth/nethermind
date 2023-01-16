// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Benchmark.Bytecode
{
    public class BenchmarkParam
    {
        public BenchmarkParam()
        {

        }

        public BenchmarkParam(string type, string name, byte[] bytecode, byte[] expectedResult, long nominalGasCost)
        {
            Type = type;            
            Name = name;
            Bytecode = bytecode;
            ExpectedResult = expectedResult;
            NominalGasCost = nominalGasCost;
        }

        public byte[] Bytecode { get; set; }

        public byte[] ExpectedResult { get; set; }

        public string Type { get; set; }

        public string Name { get; set; }

        public long NominalGasCost { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
