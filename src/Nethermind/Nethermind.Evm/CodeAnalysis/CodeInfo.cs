// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class CodeInfo(ReadOnlyMemory<byte> code, ValueHash256? codeHash = null) : ICodeInfo, IThreadPoolWorkItem
{
    private static readonly JumpDestinationAnalyzer _emptyAnalyzer = new(Array.Empty<byte>());
    public static CodeInfo Empty { get; } = new(ReadOnlyMemory<byte>.Empty);
    public IlInfo IlMetadata { get; } = IlInfo.Empty();
    public ReadOnlyMemory<byte> Code { get; } = code;
    ReadOnlySpan<byte> ICodeInfo.CodeSpan => Code.Span;

    private readonly JumpDestinationAnalyzer _analyzer = code.Length == 0 ? _emptyAnalyzer : new JumpDestinationAnalyzer(code);

    public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer);

    public ValueHash256? CodeHash => codeHash;

    public bool ValidateJump(int destination)
    {
        return _analyzer.ValidateJump(destination);
    }

    void IThreadPoolWorkItem.Execute()
    {
        _analyzer.Execute();
    }

    public void AnalyzeInBackgroundIfRequired()
    {
        if (!ReferenceEquals(_analyzer, _emptyAnalyzer) && _analyzer.RequiresAnalysis)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
    }


    private int _callCount;
    public void NoticeExecution(IVMConfig vmConfig, ILogger logger, IReleaseSpec spec)
    {
        if (   CodeHash is null
            || vmConfig.IlEvmEnabledMode == ILMode.NO_ILVM
            || !IlMetadata.IsNotProcessed
            || Code.Length == 0)
            return;

        if (AotContractsRepository.IsWhitelisted(CodeHash.Value))
        {
            Interlocked.Exchange(ref IlMetadata.AnalysisPhase, AnalysisPhase.Processing);

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

        if (Code.Length < vmConfig.IlEvmBytecodeMinLength
            || Code.Length > (vmConfig.IlEvmBytecodeMaxLength ?? spec.MaxCodeSize)) return;


        if (Interlocked.Increment(ref _callCount) != vmConfig.IlEvmAnalysisThreshold)
            return;

        IlAnalyzer.Enqueue(this, vmConfig, logger);
    }
}
