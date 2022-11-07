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
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private EofHeader _header;
        private bool? IsEOF = null;
        private static Random _rand = new();

        public byte[] MachineCode { get; set; }
        public int SectionId { get; set; } = 0;
        public EofHeader Header => _header;

        #region EofSection Extractors
        public CodeInfo SeparateEOFSections(IReleaseSpec spec, out Span<byte> Container, out Span<byte> TypeSection, out Span<byte> CodeSection, out Span<byte> DataSection)
        {
            Container = MachineCode.AsSpan();
            if(IsEOF is null && Header is null)
            {
                IsEOF = ByteCodeValidator.IsEOFCode(Container, spec, out _header);
            }

            if (IsEOF.Value && (Header is not null))
            {
                var offsets = new [] { Header.TypeSectionOffsets, Header.CodeSectionOffsets, Header.DataSectionOffsets };
                TypeSection = MachineCode.Slice(offsets[0].Start, offsets[0].Size);
                CodeSection = MachineCode.Slice(offsets[1].Start, offsets[1].Size);
                DataSection = MachineCode.Slice(offsets[2].Start, offsets[2].Size);
                return this;
            }

            TypeSection = Span<byte>.Empty;
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

        public bool IsPrecompile => Precompile != null;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_analyzer == null)
            {
                CreateAnalyzer();
            }

            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        private void CreateAnalyzer()
        {
            (var SectionStart, var SectionSize) = Header?[SectionId] ?? (0, MachineCode.Length);
            byte[] codeToBeAnalyzed = MachineCode.Slice(SectionStart, SectionSize);
            if (codeToBeAnalyzed.Length >= SampledCodeLength)
            {
                byte push1Count = 0;

                // we check (by sampling randomly) how many PUSH1 instructions are in the code
                for (int i = 0; i < NumberOfSamples; i++)
                {
                    byte instruction = MachineCode[_rand.Next(0, codeToBeAnalyzed.Length)];

                    // PUSH1
                    if (instruction == 0x60)
                    {
                        push1Count++;
                    }
                }

                // If there are many PUSH1 ops then use the JUMPDEST analyzer.
                // The JumpdestAnalyzer can perform up to 40% better than the default Code Data Analyzer
                // in a scenario when the code consists only of PUSH1 instructions.
                _analyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(codeToBeAnalyzed) : new CodeDataAnalyzer(codeToBeAnalyzed);
            }
            else
            {
                _analyzer = new CodeDataAnalyzer(codeToBeAnalyzed);
            }
        }
    }
}
