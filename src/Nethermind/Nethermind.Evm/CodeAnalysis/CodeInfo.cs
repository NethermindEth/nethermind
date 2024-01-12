// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        public byte[] MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }
        private JumpDestinationAnalyzer? _analyzer;

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
            JumpDestinationAnalyzer analyzer = _analyzer;
            analyzer ??= CreateAnalyzer();

            return analyzer.ValidateJump(destination, isSubroutine);
        }

        /// <summary>
        /// Do sampling to choose an algo when the code is big enough.
        /// When the code size is small we can use the default analyzer.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JumpDestinationAnalyzer CreateAnalyzer()
        {
            return _analyzer = new JumpDestinationAnalyzer(MachineCode);
        }
    }
}
