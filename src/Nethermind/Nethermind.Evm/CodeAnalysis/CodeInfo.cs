// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfoFactory
    {
        public static ICodeInfo CreateCodeInfo(byte[] code, IReleaseSpec spec, ILogManager logManager = null)
        {
            var byteCodeValidator = new ByteCodeValidator(logManager);
            if(spec.IsEip3540Enabled && byteCodeValidator.ValidateEofBytecode(code, out EofHeader? header))
            {
                return new EofCodeInfo(code, header.Value);
            } else
            {
                return new CodeInfo(code);
            }
        }
    }
    public interface ICodeInfo
    {
        byte[] MachineCode { get; }
        IPrecompile? Precompile { get; }
        bool IsPrecompile => Precompile is not null;
        byte Version { get; }
        bool IsEof { get; }
        ReadOnlySpan<byte> TypeSection => Span<byte>.Empty;
        ReadOnlySpan<byte> CodeSection => MachineCode;
        ReadOnlySpan<byte> DataSection => Span<byte>.Empty;
        bool ValidateJump(int destination, bool isSubroutine);
    }

    public class EofCodeInfo : CodeInfo
    {
        private EofHeader _header;
        public override bool IsEof => true;
        public override byte Version => _header.Version;

        public ReadOnlySpan<byte> TypeSection => MachineCode.Slice(_header.TypeSection.Start, _header.TypeSection.Size);
        public override ReadOnlySpan<byte> CodeSection => MachineCode.Slice(_header.CodeSections[0].Start, _header.CodeSections.Sum(s => s.Size));
        public ReadOnlySpan<byte> DataSection => MachineCode.Slice(_header.DataSection.Start, _header.DataSection.Size);

        public EofCodeInfo(byte[] code, in EofHeader header) : base(code)
        {
            _header = header;
        }
    }

    public class CodeInfo : ICodeInfo
    {
        private const int SampledCodeLength = 10_001;
        private const int PercentageOfPush1 = 40;
        private const int NumberOfSamples = 100;
        private static Random _rand = new();

        public virtual byte Version => 0;
        public virtual bool IsEof => false;
        private ICodeInfoAnalyzer? _analyzer;

        public byte[] MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }
        public virtual ReadOnlySpan<byte> CodeSection => MachineCode;

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

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            if (_analyzer is null)
            {
                CreateAnalyzer(CodeSection.ToArray());
            }

            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        protected void CreateAnalyzer(byte[] codeToBeAnalyzed)
        {
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
                _analyzer = push1Count > PercentageOfPush1 ? new JumpdestAnalyzer(codeToBeAnalyzed) : new CodeDataAnalyzer(codeToBeAnalyzed);
            }
            else
            {
                _analyzer = new CodeDataAnalyzer(codeToBeAnalyzed);
            }
        }
    }
}
