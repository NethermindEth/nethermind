// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private static Random _rand = new();

        public byte[] MachineCode { get; set; }
        public int SectionId { get; set; } = 0;

        public bool? IsEof = null;
        public EofHeader Header => _header;

        #region EofSection Extractors
        public CodeInfo SeparateEOFSections(IReleaseSpec spec, out Span<byte> Container, out Span<byte> TypeSection, out Span<byte> CodeSection, out Span<byte> DataSection)
        {
            Container = MachineCode.AsSpan();

            if (spec.IsEip3540Enabled)
            {
                IsEof ??= ByteCodeValidator.Instance.ValidateEofStructure(Container, spec, out _header);

                if (IsEof.Value && (Header is not null))
                {
                    var offsets = new[] { Header.TypeSectionOffsets, Header.CodeSectionOffsets, Header.DataSectionOffsets };
                    if (spec.IsEip4750Enabled)
                    {
                        TypeSection = MachineCode.Slice(offsets[0].Start, offsets[0].Size);
                    }
                    else
                    {
                        TypeSection = Span<byte>.Empty;
                    }
                    CodeSection = MachineCode.Slice(offsets[1].Start, offsets[1].Size);
                    DataSection = MachineCode.Slice(offsets[2].Start, offsets[2].Size);
                    return this;
                }
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

            return _analyzer.ValidateJump(destination, isSubroutine, SectionId);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        private void CreateAnalyzer(IReleaseSpec spec)
        {
            var (codeSectionStart, codeSectionSize) = IsEof.HasValue && IsEof.Value ? Header.CodeSectionOffsets : (0, MachineCode.Length);
            var codeToBeAnalyzed = MachineCode.Slice(codeSectionStart, codeSectionSize);

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
                _analyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(codeToBeAnalyzed, Header, spec) : new CodeDataAnalyzer(codeToBeAnalyzed, Header, spec);
            }
            else
            {
                _analyzer = new CodeDataAnalyzer(codeToBeAnalyzed, Header, spec);
            }
        }
    }
}
