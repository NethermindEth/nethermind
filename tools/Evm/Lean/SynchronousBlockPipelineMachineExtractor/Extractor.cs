// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor;

/// <summary>Extraction boundary kept closed until the semantic admission gates are discharged.</summary>
internal static class Extractor
{
    internal static void Extract(string repositoryRoot, string outputDirectory) =>
        SourceAudit.RefuseExtraction(repositoryRoot, outputDirectory);
}
