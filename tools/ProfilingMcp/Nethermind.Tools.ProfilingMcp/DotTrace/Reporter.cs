// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Xml.Linq;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

/// <summary>Time spent in one method, aggregated over all call stacks, as reported by dotTrace Reporter.</summary>
/// <param name="TotalTime">Time in the method and its callees, in milliseconds. A delta when produced by a comparison.</param>
/// <param name="OwnTime">Time in the method body alone, in milliseconds. A delta when produced by a comparison.</param>
/// <param name="Calls">Call count; only present for Tracing and LineByLine snapshots.</param>
/// <param name="Samples">Sample count; only present for Sampling snapshots.</param>
public sealed record FunctionStat(string Name, double TotalTime, double OwnTime, long? Calls, long? Samples, IReadOnlyList<CallStackStat> CallStacks);

/// <param name="Frames">Frames from the outermost caller down to the method itself.</param>
public sealed record CallStackStat(IReadOnlyList<string> Frames, double TotalTime, double OwnTime, long? Calls, long? Samples);

/// <summary>
/// Runs dotTrace's <c>Reporter.exe</c> over performance snapshots and parses its XML output.
/// </summary>
/// <remarks>
/// Reporter only lists methods matching the regular expressions in its pattern file, so <c>.*</c> is used to get every method.
/// It does not support Timeline (<c>*.dtt</c>) snapshots, and it exits with code 1024 even after writing a valid report,
/// so success is judged by the report file being written.
/// </remarks>
public sealed class Reporter(DotTraceLocator locator)
{
    private const string AllMethods = ".*";

    /// <summary>Returns every method in the snapshot, reusing a cached report when one exists.</summary>
    public async Task<IReadOnlyList<FunctionStat>> GetAllMethodsAsync(string snapshot, CancellationToken cancellationToken)
    {
        string cached = snapshot + ".all.xml";
        if (!File.Exists(cached))
            await RunAsync(["report", snapshot], AllMethods, withCallStacks: false, cached, cancellationToken);

        return Parse(XDocument.Load(cached));
    }

    /// <summary>Returns the methods whose full names match <paramref name="methodPattern"/>, with every distinct call stack that reaches them.</summary>
    public async Task<IReadOnlyList<FunctionStat>> GetCallStacksAsync(string snapshot, string methodPattern, CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetTempPath(), $"dottrace-stacks-{Guid.NewGuid():N}.xml");
        try
        {
            await RunAsync(["report", snapshot], methodPattern, withCallStacks: true, output, cancellationToken);
            return Parse(XDocument.Load(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    /// <summary>Returns per-method time deltas from <paramref name="baseline"/> to <paramref name="current"/>.</summary>
    public async Task<IReadOnlyList<FunctionStat>> CompareAsync(string baseline, string current, CancellationToken cancellationToken)
    {
        string output = Path.Combine(Path.GetTempPath(), $"dottrace-compare-{Guid.NewGuid():N}.xml");
        try
        {
            await RunAsync(["compare", baseline, current], AllMethods, withCallStacks: false, output, cancellationToken);
            return Parse(XDocument.Load(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    internal static IReadOnlyList<FunctionStat> Parse(XDocument report)
    {
        List<FunctionStat> functions = [];
        foreach (XElement function in report.Root!.Elements("Function"))
        {
            List<CallStackStat> stacks = [];
            foreach (XElement instance in function.Elements("Instance"))
            {
                if (instance.Attribute("CallStack")?.Value is not { } callStack)
                    continue;

                stacks.Add(new CallStackStat(
                    callStack.Split('/'),
                    ParseDouble(instance, "TotalTime"),
                    ParseDouble(instance, "OwnTime"),
                    ParseLong(instance, "Calls"),
                    ParseLong(instance, "Samples")));
            }

            functions.Add(new FunctionStat(
                function.Attribute("FQN")?.Value ?? "?",
                ParseDouble(function, "TotalTime"),
                ParseDouble(function, "OwnTime"),
                ParseLong(function, "Calls"),
                ParseLong(function, "Samples"),
                stacks));
        }

        return functions;
    }

    private static double ParseDouble(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { } value ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

    private static long? ParseLong(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value is { } value ? long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : null;

    private async Task RunAsync(string[] command, string methodPattern, bool withCallStacks, string output, CancellationToken cancellationToken)
    {
        foreach (string snapshot in command.AsSpan(1))
        {
            if (!File.Exists(snapshot))
                throw new FileNotFoundException($"Snapshot not found: {snapshot}");
            if (snapshot.EndsWith(".dtt", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("dotTrace Reporter cannot read Timeline snapshots (*.dtt). Profile with the Sampling, Tracing or LineByLine type instead.");
        }

        string patternFile = Path.Combine(Path.GetTempPath(), $"dottrace-pattern-{Guid.NewGuid():N}.xml");
        string callStacks = withCallStacks ? " PrintCallstacks=\"Full\"" : "";
        await File.WriteAllTextAsync(patternFile, $"<Patterns><Pattern{callStacks}>{SecurityElement.Escape(methodPattern)}</Pattern></Patterns>", cancellationToken);
        File.Delete(output);

        try
        {
            ProcessStartInfo startInfo = new(await locator.GetReporterPathAsync(cancellationToken))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--no-check-for-updates");
            foreach (string arg in command)
                startInfo.ArgumentList.Add(arg);
            startInfo.ArgumentList.Add($"--pattern={patternFile}");
            startInfo.ArgumentList.Add($"--save-to={output}");
            startInfo.ArgumentList.Add("--overwrite");

            using Process process = Process.Start(startInfo)!;
            await using CancellationTokenRegistration kill = cancellationToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It has already exited.
                }
            });
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (!File.Exists(output))
                throw new InvalidOperationException($"Reporter exited with code {process.ExitCode} without writing a report.\n{await stdout}\n{await stderr}");
        }
        finally
        {
            File.Delete(patternFile);
        }
    }
}
