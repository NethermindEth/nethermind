// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : IThreadPoolWorkItem
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

        public CodeInfo(IPrecompile precompile)
        {
            Precompile = precompile;
            MachineCode = Array.Empty<byte>();
            _analyzer = _emptyAnalyzer;
        }

        public bool ValidateJump(int destination, bool isSubroutine)
        {
            return _analyzer.ValidateJump(destination, isSubroutine);
        }

        void IThreadPoolWorkItem.Execute()
        {
            _analyzer.Execute();
        }
    }
}
