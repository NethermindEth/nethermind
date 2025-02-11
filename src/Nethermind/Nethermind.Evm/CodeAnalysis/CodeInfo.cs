// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Config;
using Nethermind.Logging;
using IlevmMode = int;
using Nethermind.Core;
using Nethermind.Core.Extensions;
namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : IThreadPoolWorkItem
    {
        public Address? Address { get; init; }
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }

        // IL-EVM
        private int _callCount;

        public void NoticeExecution(IVMConfig vmConfig, ILogger logger)
        {
            // IL-EVM info already created
            if (_callCount > Math.Max(vmConfig.PartialAotThreshold, vmConfig.PatternMatchingThreshold))
                return;

            Interlocked.Increment(ref _callCount);
            // use Interlocked just in case of concurrent execution to run it only once
            IlevmMode mode = vmConfig.IsPartialAotEnabled && _callCount == vmConfig.PartialAotThreshold
                ? ILMode.PARTIAL_AOT_MODE
                : vmConfig.IsPatternMatchingEnabled && _callCount == vmConfig.PatternMatchingThreshold
                    ? ILMode.PATTERN_BASED_MODE
                    : ILMode.NO_ILVM;

            if (mode == ILMode.NO_ILVM || IlInfo.Mode.HasFlag(mode))
                return;

            IlAnalyzer.Enqueue(this, mode, vmConfig, logger);

        }
        private readonly JumpDestinationAnalyzer _analyzer;
        private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
        public static CodeInfo Empty { get; } = new CodeInfo(Array.Empty<byte>(), null);

        public CodeInfo(byte[] code, Address source = null)
        {
            Address = source;
            MachineCode = code;
            IlInfo = IlInfo.Empty(MachineCode.Length);
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(ReadOnlyMemory<byte> code, Address source = null)
        {
            Address = source;
            MachineCode = code;
            IlInfo = IlInfo.Empty(MachineCode.Length);
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public bool IsPrecompile => Precompile is not null;
        public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer) && !IsPrecompile;

        /// <summary>
        /// Gets information whether this code info has IL-EVM optimizations ready.
        /// </summary>
        internal IlInfo? IlInfo { get; set; }

        public CodeInfo(IPrecompile precompile)
        {
            Address = null;
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
