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

using System;
using System.Collections;
using System.Reflection.PortableExecutable;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private EofHeader _header;
        private bool? isEof = null;
        private static Random _rand = new();

        public byte[] MachineCode { get; set; }
        public EofHeader Header => _header;

        #region EofSection Extractors
        public CodeInfo SeparateEOFSections(IReleaseSpec spec, out Span<byte> Container, out Span<byte> CodeSection, out Span<byte> DataSection)
        {
            Container = MachineCode.AsSpan();
            if (spec.IsEip3540Enabled)
            {
                if (isEof is null && _header is null)
                {
                    isEof = ByteCodeValidator.ValidateEofStrucutre(MachineCode, spec, out _header);
                }

                if (isEof.Value)
                {
                    var codeSectionOffsets = Header.CodeSectionOffsets;
                    CodeSection = MachineCode.Slice(codeSectionOffsets.Start, codeSectionOffsets.Size);
                    var dataSectionOffsets = Header.DataSectionOffsets;
                    DataSection = MachineCode.Slice(dataSectionOffsets.Start, dataSectionOffsets.Size);
                    return this;
                }
            }
            CodeSection = MachineCode.AsSpan();
            DataSection = Span<byte>.Empty;
            return this;
        }
        #endregion

        public IPrecompile? Precompile { get; set; }
        private ICodeInfoAnalyzer? _analyzer;

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
        }

        public bool IsPrecompile => Precompile is not null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
        }

        public bool ValidateJump(int destination, bool isSubroutine, IReleaseSpec spec)
        {
            if (_analyzer is null)
            {
                CreateAnalyzer(spec);
            }

            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        private void CreateAnalyzer(IReleaseSpec spec)
        {
            var codeSectionOffsets = isEof.HasValue && isEof.Value == true ? Header.CodeSectionOffsets : (0, MachineCode.Length);
            var codeToBeAnalyzed = MachineCode.Slice(codeSectionOffsets.Item1, codeSectionOffsets.Item2);
            if (codeToBeAnalyzed.Length >= SampledCodeLength)
            {
                byte push1Count = 0;

                // we check (by sampling randomly) how many PUSH1 instructions are in the code
                for (int i = 0; i < NumberOfSamples; i++)
                {
                    byte instruction = codeToBeAnalyzed[_rand.Next(0, codeToBeAnalyzed.Length)];

                    // PUSH1
                    if (instruction == 0x60)
                    {
                        push1Count++;
                    }
                }

                // If there are many PUSH1 ops then use the JUMPDEST analyzer.
                // The JumpdestAnalyzer can perform up to 40% better than the default Code Data Analyzer
                // in a scenario when the code consists only of PUSH1 instructions.
                _analyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(codeToBeAnalyzed, spec) : new CodeDataAnalyzer(codeToBeAnalyzed, spec);
            }
            else
            {
                _analyzer = new CodeDataAnalyzer(codeToBeAnalyzed, spec);
            }
        }
    }
}
