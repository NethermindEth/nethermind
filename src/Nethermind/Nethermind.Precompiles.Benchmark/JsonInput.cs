// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Precompiles.Benchmark
{
    public class JsonInput
    {
        public byte[]? Input { get; set; }
        public byte[]? Expected { get; set; }
        public string? Name { get; set; }
        public long Gas { get; set; }
        public bool NoBenchmark { get; set; }
    }
}
