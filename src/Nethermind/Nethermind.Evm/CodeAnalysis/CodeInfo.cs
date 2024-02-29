// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : IThreadPoolWorkItem
    {
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }

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
                IlAnalyzer.StartAnalysis(MachineCode, this);
            }
        }
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

        internal void SetIlInfo(IlInfo info) => _il = info;
    }
}
