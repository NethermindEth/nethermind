// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>Keeps one operation of a kind on one process at a time, across every server that profiles it.</summary>
/// <remarks>
/// The lock is a file opened exclusively in the temp folder, which all servers profiling a process share: the user's temp folder on a host,
/// and the target's temp folder in a sidecar, also across sidecars of different versions. The file stays after release, because deleting it
/// would let a server lock a file that another has already replaced.
/// </remarks>
internal static class ProcessLock
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <param name="kind">What is serialized, e.g. <c>heap</c> for heap reads.</param>
    /// <param name="timeout">How long to wait for another server's operation to end.</param>
    public static async Task<IDisposable> AcquireAsync(string kind, int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"profmcp-{kind}-{pid}.lock");
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(Poll, cancellationToken);
            }
            catch (IOException e)
            {
                throw new InvalidOperationException($"Another session has been running {kind} on process {pid} for {timeout.TotalSeconds:N0} s; try again when it ends.", e);
            }
        }
    }
}
