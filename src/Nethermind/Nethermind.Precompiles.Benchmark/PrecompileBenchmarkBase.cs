// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
