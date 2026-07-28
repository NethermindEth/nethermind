// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IlEvm;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis.IlEvm;

/// <summary>
/// Coverage diagnostics against REAL deployed bytecode (dropped at /tmp/diag-bytecode.hex by
/// the operator): reports how much of the contract the region compiler actually covers under
/// PRODUCTION economics, and which opcodes fragment the regions — the to-do list for raising
/// coverage. Writes the report to /tmp/ilevm-diag.txt.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IlEvmRealBytecodeDiagnostics
{
    [Test]
    public void Diagnose_RealBytecode_ReportsRegionCoverage()
    {
        const string inputPath = "/tmp/diag-bytecode.hex";
        if (!File.Exists(inputPath))
        {
            Assert.Ignore("No /tmp/diag-bytecode.hex present — diagnostics skipped.");
            return;
        }

        byte[] code = Bytes.FromHexString(File.ReadAllText(inputPath).Trim());
        // The node's CURRENT mainnet view, not GetSpec(long.MaxValue): future specs enable
        // EIP-8024, which disables the analyzer entirely and zeroes this report.
        IReleaseSpec spec = Nethermind.Specs.Forks.Osaka.Instance;

        AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, spec);

        int compilableBlocks = 0;
        Dictionary<Instruction, int> cutters = [];
        foreach (BasicBlock block in analyzed.Blocks)
        {
            if (block.IsCompilable)
            {
                compilableBlocks++;
            }
            else
            {
                Instruction cutter = (Instruction)code[block.Start];
                cutters[cutter] = cutters.GetValueOrDefault(cutter) + 1;
            }
        }

        // Mirror IlEvm.Compile exactly, under PRODUCTION economics (the node's view).
        long registerizedBefore = IlSegmentCompiler.RegionsRegisterized;
        long fallbackBefore = IlSegmentCompiler.RegionsFallbackV40;
        IlCompiledSegment?[] segments = new IlCompiledSegment?[analyzed.BlockCount];
        int segmentCount = 0;
        int regionsAttempted = 0;
        int blockIndex = 0;
        while (blockIndex < analyzed.BlockCount)
        {
            if (analyzed.Blocks[blockIndex].IsCompilable)
            {
                regionsAttempted++;
                blockIndex += IlSegmentCompiler.TryCompileRegion(code, analyzed, blockIndex, spec, segments, out int regionSegments);
                segmentCount += regionSegments;
            }
            else
            {
                blockIndex++;
            }
        }

        int coveredOps = 0;
        int maxEntryPops = 0;
        long totalEntryPops = 0;
        int segmentTally = 0;
        foreach (IlCompiledSegment? segment in segments)
        {
            if (segment is null)
                continue;
            coveredOps += segment.OpCount;
            maxEntryPops = Math.Max(maxEntryPops, segment.StackRequired);
            totalEntryPops += segment.StackRequired;
            segmentTally++;
        }

        StringBuilder report = new();
        report.AppendLine($"code bytes:          {code.Length}");
        report.AppendLine($"blocks:              {analyzed.BlockCount} (compilable {compilableBlocks}, interpreter-only {analyzed.BlockCount - compilableBlocks})");
        report.AppendLine($"regions attempted:   {regionsAttempted}");
        report.AppendLine($"entry segments:      {segmentCount}");
        report.AppendLine($"entry-block ops sum: {coveredOps}");
        report.AppendLine($"entry pops (boundary traffic): max={maxEntryPops} avg={(segmentTally > 0 ? (double)totalEntryPops / segmentTally : 0):F1}");
        report.AppendLine($"regions registerized (v4.1): {IlSegmentCompiler.RegionsRegisterized - registerizedBefore}, fallback (v4.0): {IlSegmentCompiler.RegionsFallbackV40 - fallbackBefore}");
        report.AppendLine($"compile failures:    {Nethermind.Evm.CodeAnalysis.IlEvm.IlEvm.ContractCompilationFailures}");
        report.AppendLine("region cutters (interpreter-only block leaders):");
        foreach ((Instruction cutter, int count) in cutters.OrderByDescending(static pair => pair.Value).Take(15))
            report.AppendLine($"  {cutter,-18} ×{count}");

        File.WriteAllText("/tmp/ilevm-diag.txt", report.ToString());
        Assert.That(segmentCount, Is.GreaterThanOrEqualTo(0), "diagnostics always pass; see /tmp/ilevm-diag.txt");
    }
}
