// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp.Analysis;

/// <summary>Time spent in one method of a CPU trace, aggregated over all call stacks.</summary>
/// <param name="TotalTime">Time in the method and its callees, in milliseconds. A delta when produced by a comparison.</param>
/// <param name="OwnTime">Time in the method body alone, in milliseconds. A delta when produced by a comparison.</param>
/// <param name="Samples">Number of samples in the method; null for a comparison.</param>
public sealed record FunctionStat(string Name, double TotalTime, double OwnTime, long? Samples, IReadOnlyList<CallStackStat> CallStacks);

/// <param name="Frames">Frames from the outermost caller down to the method itself.</param>
public sealed record CallStackStat(IReadOnlyList<string> Frames, double TotalTime, double OwnTime, long? Samples);
