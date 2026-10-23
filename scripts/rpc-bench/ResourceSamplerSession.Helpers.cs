// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

internal sealed class ResourceSamplerSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;

    internal ResourceSamplerSession(string container, string output) =>
        _task = Task.Run(async () =>
        {
            if (container.Length == 0 || output.Length == 0) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var result = await BenchmarkProcess.RunAsync("docker", ["inspect", "--format", "{{.Id}}", container], timeout.Token);
                if (result.Code != 0) throw new IOException("container not found");
                var id = result.Output.Trim();
                var roots = new[] { $"/sys/fs/cgroup/system.slice/docker-{id}.scope", $"/sys/fs/cgroup/docker/{id}", $"/sys/fs/cgroup/kubepods/docker-{id}.scope" };
                var root = roots.FirstOrDefault(path => File.Exists(Path.Combine(path, "cpu.stat"))) ?? throw new IOException("no cgroup v2 directory found for the container");
                var summary = ResourceSampling.Sample(root, () => _stop.IsCancellationRequested, () => _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(.25)));
                ResourceSampling.Write(output, summary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { Console.Error.WriteLine($"resource sampling unavailable: {exception.Message}"); }
        });

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _task; }
        finally { _stop.Dispose(); }
    }
}
