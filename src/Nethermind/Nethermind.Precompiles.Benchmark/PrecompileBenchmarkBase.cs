// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;

namespace Nethermind.Precompiles.Benchmark
{
    public abstract class PrecompileBenchmarkBase
    {
        protected abstract IEnumerable<IPrecompile> Precompiles { get; }

        protected abstract string InputsDirectory { get; }

        public readonly struct Param(IPrecompile precompile, string name, byte[] bytes, byte[]? expected)
        {
            public IPrecompile Precompile { get; } = precompile ?? throw new ArgumentNullException(nameof(precompile));

            public byte[] Bytes { get; } = bytes;

            public byte[]? ExpectedResult { get; } = expected;

            public string Name { get; } = name;

            public override string ToString() => Name;
        }

        public IEnumerable<Param> Inputs
        {
            get
            {
                foreach (IPrecompile precompile in Precompiles)
                {
                    List<Param> inputs = [];
                    foreach (string file in Directory.GetFiles($"{InputsDirectory}/current", "*.csv", SearchOption.TopDirectoryOnly))
                    {
                        // take only first line from each file
                        inputs.AddRange(File.ReadAllLines(file)
                            .Select(LineToTestInput).Take(1).ToArray()
                            .Select(i => new Param(precompile, file, i, null)));
                    }

                    foreach (string file in Directory.GetFiles($"{InputsDirectory}/current", "*.json", SearchOption.TopDirectoryOnly))
                    {
                        EthereumJsonSerializer jsonSerializer = new();
                        JsonInput[] jsonInputs = jsonSerializer.Deserialize<JsonInput[]>(File.ReadAllText(file));
                        IEnumerable<Param> parameters = jsonInputs.Select(i => new Param(precompile, i.Name!, i.Input!, i.Expected));
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
            => Bytes.FromHexString(line.Split(',')[0]);

        [Benchmark(Baseline = true)]
        public (ReadOnlyMemory<byte>, bool) Baseline()
            => Input.Precompile.Run(Input.Bytes, Berlin.Instance);
    }
}
