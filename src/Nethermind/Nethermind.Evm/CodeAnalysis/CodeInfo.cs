// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Evm.CodeAnalysis.IL;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : IThreadPoolWorkItem
    {
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }


        // IL-EVM
        private int _callCount;

        public async void NoticeExecution()
        {
            // IL-EVM info already created
            if (_callCount > IlAnalyzer.IlCompilerThreshold)
                return;

            // use Interlocked just in case of concurrent execution to run it only once
            IlInfo.ILMode mode = Interlocked.Increment(ref _callCount) == IlAnalyzer.CompoundOpThreshold
                ? IlInfo.ILMode.PatternMatching
                : _callCount == IlAnalyzer.IlCompilerThreshold ? IlInfo.ILMode.SubsegmentsCompiling : IlInfo.ILMode.NoIlvm;
            await IlAnalyzer.StartAnalysis(this, mode);
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
        public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer) && !IsPrecompile;

        /// <summary>
        /// Gets information whether this code info has IL-EVM optimizations ready.
        /// </summary>
        internal IlInfo? IlInfo { get; set; } = IlInfo.Empty;

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
    }
}
