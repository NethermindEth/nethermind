// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Evm.CodeAnalysis.IL;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo
    {
        public byte[] MachineCode { get; set; }
        public IPrecompile? Precompile { get; set; }

        private JumpDestinationAnalyzer? _analyzer;

        // IL-EVM
        private volatile IlInfo? _il;
        private int _callCount;

        public void NoticeExecution()
        {
            // IL-EVM info already created
            if (_il != null)
                return;

            // use Interlocked just in case of concurrent execution to run it only once
            if (Interlocked.Increment(ref _callCount) == IlAnalyzer.IlAnalyzerThreshold)
            {
                IlAnalyzer.StartAnalysis(this);
            }
        }

        public CodeInfo(byte[] code)
        {
            MachineCode = code;
        }

        public bool IsPrecompile => Precompile is not null;

        /// <summary>
        /// Gets information whether this code info has IL-EVM optimizations ready.
        /// </summary>
        internal IlInfo? IlInfo
        {
            get
            {
                IlInfo? il = _il;
                return il != null && !ReferenceEquals(il, IlInfo.NoIlEVM) ? il : null;
            }
        }

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

        internal void SetIlInfo(IlInfo info) => _il = info;
    }
}
