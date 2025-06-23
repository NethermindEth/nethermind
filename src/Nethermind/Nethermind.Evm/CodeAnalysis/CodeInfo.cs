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
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Collections;
using System.Threading.Tasks;
using Nethermind.Evm.CodeAnalysis.IL.Delegates;
namespace Nethermind.Evm.CodeAnalysis
{
    public class CodeInfo : IThreadPoolWorkItem
    {
        public ValueHash256? Codehash { get; set; }
        public ReadOnlyMemory<byte> MachineCode { get; }
        public IPrecompile? Precompile { get; set; }

        // IL-EVM
        private int _callCount;

        public void NoticeExecution(IVMConfig vmConfig, ILogger logger, IReleaseSpec spec)
        {
            if (Codehash is null || vmConfig.IlEvmEnabledMode == ILMode.NO_ILVM || !IlInfo.IsNotProcessed)
                return;

            if (AotContractsRepository.IsWhitelisted(Codehash.Value))
            {
                Interlocked.Exchange(ref IlInfo.AnalysisPhase, AnalysisPhase.Processing);

                //Todo : Move this to aot-repository
                Interlocked.Decrement(ref AotContractsRepository.WhiteListCount);

                Task.Run(() =>
                {
                    IlAnalyzer.Analyse(this, vmConfig.IlEvmEnabledMode, vmConfig, logger);
                });
                return;
            }

            // avoid falling back to dynamic dynamic AOT if all whitelisted contracts are processed
            if (vmConfig.IlEvmAllowedContracts.Length > 0) return;

            if (MachineCode.Length < vmConfig.IlEvmBytecodeMinLength
                || MachineCode.Length > (vmConfig.IlEvmBytecodeMaxLength ?? spec.MaxCodeSize)) return;


            if(Interlocked.Increment(ref _callCount) != vmConfig.IlEvmAnalysisThreshold)
                return;

            IlAnalyzer.Enqueue(this, vmConfig, logger);
        }

        private readonly JumpDestinationAnalyzer _analyzer;
        private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
        public static CodeInfo Empty { get; } = new CodeInfo(Array.Empty<byte>(), null);

        public CodeInfo(byte[] code, ValueHash256? codeHash = null)
        {
            Codehash = codeHash;

            if (codeHash is not null && AotContractsRepository.TryGetIledCode(codeHash.Value, out ILEmittedMethod ilCode))
            {
                Metrics.IncrementIlvmAotCacheTouched();
                IlInfo.PrecompiledContract = ilCode;
                IlInfo.AnalysisPhase = AnalysisPhase.Completed;
            }

            MachineCode = code;
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public CodeInfo(ReadOnlyMemory<byte> code, ValueHash256? codeHash = null)
        {
            Codehash = codeHash;

            if (codeHash is not null && AotContractsRepository.TryGetIledCode(codeHash.Value, out ILEmittedMethod ilCode))
            {
                Metrics.IncrementIlvmAotCacheTouched();
                IlInfo.PrecompiledContract = ilCode;
                IlInfo.AnalysisPhase = AnalysisPhase.Completed;
            }

            MachineCode = code;
            _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);
        }

        public bool IsPrecompile => Precompile is not null;
        public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer) && !IsPrecompile;

        /// <summary>
        /// Gets information whether this code info has IL-EVM optimizations ready.
        /// </summary>
        internal IlInfo? IlInfo { get; set; } = IlInfo.Empty();

        public CodeInfo(IPrecompile precompile)
        {
            Codehash = null;
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
