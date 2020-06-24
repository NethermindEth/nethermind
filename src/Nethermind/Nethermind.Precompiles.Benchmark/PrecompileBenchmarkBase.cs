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

namespace Nethermind.Precompiles.Benchmark
{
    [SuppressMessage("ReSharper", "UnassignedGetOnlyAutoProperty")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    public abstract class PrecompileBenchmarkBase
    {
        protected abstract IPrecompile[] Precompiles { get; }

        protected abstract string InputsDirectory { get; }
        
        public readonly struct Param
        {
            public IPrecompile Precompile { get; }

            public Param(IPrecompile precompile, byte[] bytes)
            {
                Precompile = precompile ?? throw new ArgumentNullException(nameof(precompile));
                Bytes = bytes;
            }
            
            public byte[] Bytes { get; }

            public override string ToString()
            {
                return $"b[{Bytes.Length.ToString().PadLeft(4, '0')}] {Precompile.GetType().Name.Substring(0, 3)}";
            }
        }
        
        public IEnumerable<Param> Inputs 
        {
            get
            {
                foreach (IPrecompile precompile in Precompiles)
                {
                    List<byte[]> inputs = new List<byte[]>();
                    foreach (var file in Directory.GetFiles($"{InputsDirectory}/current", "*.csv", SearchOption.TopDirectoryOnly))
                    {
                        // take only first line from each file
                        inputs.AddRange(File.ReadAllLines(file)
                            .Select(LineToTestInput).Take(1).ToArray());
                    }
                
                    foreach (var input in inputs.OrderBy(i => i.Length))
                    {
                        yield return new Param(precompile, input);
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
        public (byte[], bool) Baseline()
        {
            return Input.Precompile.Run(Input.Bytes);
        }
    }
}