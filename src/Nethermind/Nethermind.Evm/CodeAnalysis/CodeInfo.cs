// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

using System.Runtime.CompilerServices;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : ICodeInfo, IThreadPoolWorkItem
    {
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }
        private readonly JumpDestinationAnalyzer _analyzer;
        private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
        public static CodeInfo Empty { get; } = new CodeInfo(Array.Empty<byte>());

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(ReadOnlyMemory<byte> code)
        {
            MachineCode = code;
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public bool IsPrecompile => Precompile is not null;
        public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer) && !IsPrecompile;

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
            _analyzer = _emptyAnalyzer;
        }

        public bool ValidateJump(int destination)
        {
            return _analyzer.ValidateJump(destination);
        }

        void IThreadPoolWorkItem.Execute()
        {
            _analyzer.Execute();
        }

        public void AnalyseInBackgroundIfRequired()
        {
            if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && _analyzer.RequiresAnalysis)
            {
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            }
        }

        public SectionHeader CodeSectionOffset(int idx)
        {
            throw new NotImplementedException();
        }

        public SectionHeader? ContainerSectionOffset(int idx)
        {
            throw new NotImplementedException();
        }
    }
}
