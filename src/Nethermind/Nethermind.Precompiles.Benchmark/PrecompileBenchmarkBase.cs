// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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

            public long Gas(IReleaseSpec releaseSpec) =>
                precompile.BaseGasCost(releaseSpec) + precompile.DataGasCost(Bytes, releaseSpec);

            public override string ToString() => Name;
        }

        public IEnumerable<Param> Inputs
        {
            get
            {
                foreach (IPrecompile precompile in Precompiles)
                {
                    List<Param> inputs = [];
                    var inputsDir = Path.Combine(AppContext.BaseDirectory, InputsDirectory, "current");

                    foreach (string file in Directory.GetFiles(inputsDir, "*.csv", SearchOption.TopDirectoryOnly))
                    {
                        // take only first line from each file
                        inputs.AddRange(File.ReadAllLines(file)
                            .Select(LineToTestInput).Take(1).ToArray()
                            .Select(i => new Param(precompile, file, i, null)));
                    }

                    foreach (string file in Directory.GetFiles(inputsDir, "*.json", SearchOption.TopDirectoryOnly))
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
            => Input.Precompile.Run(Input.Bytes, Cancun.Instance);
    }
}
