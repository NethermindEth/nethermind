// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using IlevmMode = int;

[assembly: InternalsVisibleTo("Nethermind.Evm.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmarks")]
namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Provides
/// </summary>
public static class IlAnalyzer
{
    private static readonly ConcurrentQueue<CodeInfo> _queue = new();
    private static int tasksRunningCount = 0;
    public static void Enqueue(CodeInfo codeInfo, IVMConfig config, ILogger logger)
    {
        if(codeInfo.IlInfo.AnalysisPhase is not AnalysisPhase.NotStarted)
        {
            return;
        }

        codeInfo.IlInfo.AnalysisPhase = AnalysisPhase.Queued;
        _queue.Enqueue(codeInfo);

        if (config.IlEvmAnalysisQueueMaxSize <= _queue.Count)
        {
            if(tasksRunningCount < config.IlEvmAnalysisQueueMaxSize)
            {
                Task.Run(() => {
                    Interlocked.Increment(ref tasksRunningCount);
                    AnalyzeQueue(config, logger);
                    Interlocked.Decrement(ref tasksRunningCount);
                });
            }
        }
    }

    private static void AnalyzeQueue(IVMConfig config, ILogger logger)
    {
        int itemsLeft = _queue.Count;
        while (itemsLeft-- > 0 && _queue.TryDequeue(out CodeInfo worklet))
        {
            worklet.IlInfo.AnalysisPhase = AnalysisPhase.Processing;
            try
            {
                Analyse(worklet, config.IlEvmEnabledMode, config, logger);
                worklet.IlInfo.AnalysisPhase = AnalysisPhase.Completed;
            } catch
            {
                worklet.IlInfo.AnalysisPhase = AnalysisPhase.Failed;
            }
        }
    }

    private static Dictionary<Type, IPatternChunk> _patterns = new Dictionary<Type, IPatternChunk>();
    internal static void AddPattern(IPatternChunk handler)
    {
        lock (_patterns)
        {
            _patterns[handler.GetType()] = handler;
        }
    }
    internal static void AddPattern<T>() where T : IPatternChunk
    {
        var handler = Activator.CreateInstance<T>();
        lock (_patterns)
        {
            _patterns[typeof(T)] = handler;
        }
    }
    internal static T GetPatternHandler<T>() where T : IPatternChunk
    {
        lock (_patterns)
        {
            return (T)_patterns[typeof(T)];
        }
    }

    public static void Initialize()
    {
        Type[] InstructionChunks = typeof(IPatternChunk).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(IPatternChunk))).ToArray();
        foreach (var chunkType in InstructionChunks)
        {
            _patterns[chunkType] = (IPatternChunk)Activator.CreateInstance(chunkType);
        }
    }

    public static IEnumerable<(int, Instruction,  OpcodeMetadata)> EnumerateOpcodes(byte[] machineCode, Range? slice = null)
    {
        slice ??= 0..machineCode.Length;
        OpcodeMetadata metadata = default;
        for (int i = slice.Value.Start.Value; i < slice.Value.End.Value; i += 1 + metadata.AdditionalBytes)
        {
            Instruction opcode = (Instruction)machineCode[i];
            metadata = OpcodeMetadata.GetMetadata(opcode);
            yield return (i, opcode, metadata);
        }
    }

    /// <summary>
    /// For now, return null always to default to EVM.
    /// </summary>
    public static void Analyse(CodeInfo codeInfo, IlevmMode mode, IVMConfig vmConfig, ILogger logger)
    {
        Metrics.IlvmContractsAnalyzed++;
        switch (mode)
        {
            case ILMode.PATTERN_BASED_MODE:
                CheckPatterns(codeInfo.MachineCode.ToArray(), codeInfo.IlInfo);
                break;
        }
    }

    internal static void CheckPatterns(byte[] bytecode, IlInfo ilinfo)
    {
        ilinfo.IlevmChunks = new InstructionChunk[bytecode.Length];
        var strippedBytecode = EnumerateOpcodes(bytecode).ToArray();

        foreach ((Type _, IPatternChunk chunkHandler) in _patterns)
        {
            for (int i = 0; i < strippedBytecode.Length - chunkHandler.Pattern.Length + 1; i++)
            {
                bool found = true;
                for (int j = 0; j < chunkHandler.Pattern.Length && found; j++)
                {
                    found = ((byte)strippedBytecode[i + j].Item2 == chunkHandler.Pattern[j]);
                }

                if (found)
                {
                    ilinfo.AddMapping(strippedBytecode[i].Item1, chunkHandler);
                    i += chunkHandler.Pattern.Length - 1;
                }
            }
        }
    }
}
