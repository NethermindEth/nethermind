//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark
{
    [SuppressMessage("ReSharper", "UnassignedGetOnlyAutoProperty")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    public abstract class PrecompileBenchmarkBase
    {
        protected abstract IEnumerable<IPrecompile> Precompiles { get; }

        protected abstract string InputsDirectory { get; }

        public readonly struct Param
        {
            public IPrecompile Precompile { get; }

            public Param(IPrecompile precompile, string name, byte[] bytes, byte[]? expected)
            {
                Precompile = precompile ?? throw new ArgumentNullException(nameof(precompile));
                Bytes = bytes;
                Name = name;
                ExpectedResult = expected;
            }

            public byte[] Bytes { get; }

            public byte[]? ExpectedResult { get; }

            public string Name { get; }

            public override string ToString()
            {
                return Name;
            }
        }

        public IEnumerable<Param> Inputs
        {
            get
            {
                foreach (IPrecompile precompile in Precompiles)
                {
                    List<Param> inputs = new List<Param>();
                    foreach (string file in Directory.GetFiles($"{InputsDirectory}/current", "*.csv", SearchOption.TopDirectoryOnly))
                    {
                        // take only first line from each file
                        inputs.AddRange(File.ReadAllLines(file)
                            .Select(LineToTestInput).Take(1).ToArray()
                            .Select(i => new Param(precompile, file, i, null)));
                    }

                    foreach (string file in Directory.GetFiles($"{InputsDirectory}/current", "*.json", SearchOption.TopDirectoryOnly))
                    {
                        EthereumJsonSerializer jsonSerializer = new EthereumJsonSerializer();
                        var jsonInputs = jsonSerializer.Deserialize<JsonInput[]>(File.ReadAllText(file));
                        var parameters = jsonInputs.Select(i => new Param(precompile, i.Name, i.Input, i.Expected));
                        inputs.AddRange(parameters);
                    }

                    foreach (Param param in inputs.OrderBy(i => i.Name))
                    {
                        yield return param;
                    }
                }
            }
        }

        [ParamsSource(nameof(Inputs))]
        public Param Input { get; set; }

        private static byte[] LineToTestInput(string line)
        {
            return Bytes.FromHexString(line.Split(',')[0]);
        }

        [Benchmark(Baseline = true)]
        public (ReadOnlyMemory<byte>, bool) Baseline()
        {
            return Input.Precompile.Run(Input.Bytes, Berlin.Instance);
        }
    }
}
