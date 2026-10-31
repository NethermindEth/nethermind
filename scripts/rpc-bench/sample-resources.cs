#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include resource-sampling.helpers.cs
#:include benchmark-process.helpers.cs

using System.Globalization;
using System.Runtime.InteropServices;

if (args.Length < 1 || args[0] is not ("sample" or "normalize") || args.Length % 2 != 1)
{
    Console.Error.WriteLine("usage: sample-resources.cs sample --container NAME --out PATH [--interval SECONDS] | normalize --out PATH --requests COUNT");
    return 2;
}
var options = new Dictionary<string, string>();
for (var index = 1; index < args.Length; index += 2) options[args[index]] = args[index + 1];
if (!options.TryGetValue("--out", out var path) || !options.ContainsKey(args[0] == "sample" ? "--container" : "--requests"))
{
    Console.Error.WriteLine("missing required sampler arguments");
    return 2;
}
try
{
    if (args[0] == "normalize") ResourceSampling.Normalize(path, long.Parse(options["--requests"], CultureInfo.InvariantCulture));
    else
    {
        var interval = options.TryGetValue("--interval", out var seconds) ? double.Parse(seconds, CultureInfo.InvariantCulture) : 0.25;
        if (!double.IsFinite(interval) || interval < 0) throw new ArgumentException("interval must be finite and nonnegative");
        using var stop = new CancellationTokenSource();
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await BenchmarkProcess.RunAsync("docker", ["inspect", "--format", "{{.Id}}", options["--container"]], timeout.Token);
            if (result.Code != 0) throw new IOException("container not found");
            var id = result.Output.Trim();
            var roots = new[] { $"/sys/fs/cgroup/system.slice/docker-{id}.scope", $"/sys/fs/cgroup/docker/{id}", $"/sys/fs/cgroup/kubepods/docker-{id}.scope" };
            var cgroup = roots.FirstOrDefault(root => File.Exists(Path.Combine(root, "cpu.stat"))) ?? throw new IOException("no cgroup v2 directory found for the container");
            var summary = ResourceSampling.Sample(cgroup, () => stop.IsCancellationRequested, () => stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval)));
            ResourceSampling.Write(path, summary);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"resource sampling unavailable: {exception.Message}");
}
return 0;
