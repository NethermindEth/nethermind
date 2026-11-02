#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include receipt-migration.helpers.cs
#:include benchmark-process.helpers.cs
#:include rpc-script-tests.helpers.cs

using var tests = new RpcScriptTests();
const string started = "Adding transaction hashes to stored receipts, blocks 24600000 to 25489874.";
const string completed = "Transaction hashes added to stored receipts of 123 blocks, 456 bytes added.";
var log = Path.Combine(tests.DirectoryPath, "migration.log");
Task Wait(Func<CancellationToken, Task<(bool Running, string Logs)>> snapshot, CancellationToken token = default) =>
    ReceiptMigrationGate.WaitAsync(snapshot, log, TimeSpan.FromMilliseconds(200), token, TimeSpan.FromMilliseconds(5));

tests.Case("positive fresh pass completes after progress and preserves raw log", () =>
{
    var calls = 0;
    Wait(_ => Task.FromResult((true, ++calls == 1 ? started + "\nReceipt hashes 100%" : started + "\n\u001b[97m" + completed)))
        .GetAwaiter().GetResult();
    RpcScriptTests.Assert(calls == 2 && File.ReadAllText(log).Contains(completed), "gate skipped genuine completion or lost evidence");
});
foreach (var (name, running, contents, expected) in new[]
{
    ("migration error", true, started + "\nAdding transaction hashes to stored receipts failed; log queries read block bodies for the remaining blocks.", "migration failed"),
    ("error wins over completion", true, started + "\n" + completed + "\nAdding transaction hashes to stored receipts failed", "migration failed"),
    ("node exit", false, started + "\n" + completed, "node exited"),
    ("absent marker", true, started + "\nReceipt hashes 100% current 0Blk/s", "did not positively complete"),
    ("no startup marker", true, completed, "did not positively complete"),
    ("wrong marker order", true, completed + "\n" + started, "did not positively complete"),
    ("malformed completion", true, started + "\nTransaction hashes added to stored receipts of UNKNOWN blocks", "did not positively complete")
})
    tests.Case(name, () =>
    {
        try { Wait(_ => Task.FromResult((running, contents))).GetAwaiter().GetResult(); }
        catch (IOException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("gate accepted an unqualified process");
    });
tests.Case("timeout bounds a stalled probe", () =>
{
    try
    {
        Wait(async token => { await Task.Delay(TimeSpan.FromHours(1), token); return (true, started); }).GetAwaiter().GetResult();
    }
    catch (IOException exception) when (exception.Message.Contains("did not positively complete", StringComparison.Ordinal)) { return; }
    throw new InvalidOperationException("stalled probe accepted");
});
tests.Case("caller cancellation is not reported as migration timeout", () =>
{
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    try { Wait(_ => Task.FromResult((true, started)), stop.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("caller cancellation swallowed");
});
tests.Case("probe errors fail closed", () =>
{
    try { Wait(_ => throw new IOException("probe unavailable")).GetAwaiter().GetResult(); }
    catch (IOException exception) when (exception.Message == "probe unavailable") { return; }
    throw new InvalidOperationException("probe error swallowed");
});
foreach (var (name, now, hours, starting, expected) in new[]
{
    ("fits full budget", "2026-10-28T16:00:00Z", 5.5, true, ""),
    ("queued past cutoff", "2026-10-29T00:46:00Z", 1.0, true, "latest-start cutoff"),
    ("budget expires before latest-start", "2026-10-29T00:30:00Z", 5.5, true, "insufficient remaining"),
    ("setup consumed remaining budget", "2026-10-29T01:00:00Z", 5.0, false, "insufficient remaining")
})
    tests.Case(name, () =>
    {
        try { ReceiptMigrationGate.CheckBudget("2026-10-29T00:45:00Z", "2026-10-29T05:47:34Z", TimeSpan.FromHours(hours), starting, DateTimeOffset.Parse(now)); }
        catch (IOException exception) when (expected.Length > 0 && exception.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        RpcScriptTests.Assert(expected.Length == 0, "expired budget accepted");
    });
foreach (var (latest, finish) in new[] { ("bad", "2026-10-29T05:47:34Z"), ("2026-10-29T00:45:00Z", ""), ("", "2026-10-29T05:47:34Z") })
    tests.Case("reject incomplete or malformed UTC budget " + latest + " " + finish, () =>
    {
        try { ReceiptMigrationGate.CheckBudget(latest, finish, TimeSpan.Zero, true); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("bad UTC budget accepted");
    });
if (OperatingSystem.IsLinux())
{
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    var disk = Path.Combine(tests.DirectoryPath, "df");
    try
    {
        Environment.SetEnvironmentVariable("PATH", tests.DirectoryPath + Path.PathSeparator + originalPath);
        foreach (var (value, exit, expected) in new[] { ("2048", 0, ""), ("512", 0, "unsafe migration disk pressure"), ("unknown", 0, "cannot establish"), ("2048", 1, "cannot establish") })
            tests.Case("disk reserve probe " + value + " " + exit, () =>
            {
                File.WriteAllText(disk, $"#!/bin/sh\nprintf 'Avail\\n{value}\\n'\nexit {exit}\n");
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(disk, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                try { ReceiptMigrationGate.CheckSpaceAsync(tests.DirectoryPath, 1024, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException exception) when (expected.Length > 0 && exception.Message.Contains(expected, StringComparison.Ordinal)) { return; }
                RpcScriptTests.Assert(expected.Length == 0, "unsafe or unobservable disk accepted");
            });
    }
    finally { Environment.SetEnvironmentVariable("PATH", originalPath); }

    foreach (var diagnosticExit in new[] { 0, 23 })
        tests.Case("native diagnostics retain mapped and installed evidence or command failure " + diagnosticExit, () =>
        {
            var payload = Path.Combine(tests.DirectoryPath, "payload");
            var proc = Path.Combine(tests.DirectoryPath, "proc");
            var data = Path.Combine(tests.DirectoryPath, "data");
            foreach (var directory in new[] { payload, proc, data }) Directory.CreateDirectory(directory);
            var native = Path.Combine(payload, "librocksdb.so");
            var managed = Path.Combine(payload, "Nethermind.RocksDbBindings.dll");
            var unused = Path.Combine(payload, "UnusedRocksdb.dll");
            var missing = Path.Combine(payload, "missing-rocksdb.so");
            var database = Path.Combine(data, "rocksdb-data.sst");
            File.WriteAllText(database, "database fixture must not be hashed");
            File.WriteAllText(native, "native fixture");
            File.WriteAllText(managed, "managed fixture");
            File.WriteAllText(unused, "installed only fixture");
            File.WriteAllText(Path.Combine(proc, "status"), "Name:\tnethermind\nSeccomp:\t2\nNoNewPrivs:\t0\n");
            File.WriteAllText(Path.Combine(proc, "maps"), $"001-002 r-xp 0 00:00 1 {native}\n002-003 r--p 0 00:00 1 {native}\n003-004 r--p 0 00:00 2 {managed}\n004-005 r--p 0 00:00 3 {missing} (deleted)\n005-006 r--p 0 00:00 4 {database}\n");
            File.WriteAllText(Path.Combine(data, "OPTIONS-1"), "  read_io_executor_threads=2\n  async_io=true\n");
            var docker = Path.Combine(tests.DirectoryPath, "docker");
            File.WriteAllText(docker, diagnosticExit == 0 ? $$"""
                #!/bin/bash
                if [ "$1" = exec ]; then
                    script="$5"
                    script="${script//\/proc\/1/{{proc}}}"
                    script="${script//\/nethermind/{{payload}}}"
                    script="${script//\/execution-data/{{data}}}"
                    exec sh -c "$script"
                fi
                echo diagnostic-fixture
                """ : "#!/bin/sh\necho diagnostic-failure >&2\nexit 23\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                Environment.SetEnvironmentVariable("PATH", tests.DirectoryPath + Path.PathSeparator + originalPath);
                var output = Path.Combine(tests.DirectoryPath, "io-security-" + diagnosticExit + ".txt");
                NodeIoDiagnostics.CaptureAsync("fixture-container", output, CancellationToken.None).GetAwaiter().GetResult();
                var evidence = File.ReadAllText(output);
                if (diagnosticExit != 0)
                {
                    RpcScriptTests.Assert(evidence.Contains("exit=23", StringComparison.Ordinal) && evidence.Contains("diagnostic-failure", StringComparison.Ordinal), "diagnostic command failure hidden");
                    return;
                }
                string Hash(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
                RpcScriptTests.Assert(evidence.Split(Hash(native)).Length == 3 && evidence.Split(Hash(managed)).Length == 3, "native/managed installed and deduplicated mapped-path hashes missing");
                RpcScriptTests.Assert(evidence.Split(Hash(unused)).Length == 2, "installed-only payload misrepresented as mapped");
                RpcScriptTests.Assert(!evidence.Contains(Hash(database), StringComparison.Ordinal), "mapped database contents were hashed");
                RpcScriptTests.Assert(evidence.Contains("unavailable mapped path: " + missing, StringComparison.Ordinal) && evidence.Contains("(deleted)", StringComparison.Ordinal), "missing/deleted mapping evidence lost");
                RpcScriptTests.Assert(evidence.Contains("read_io_executor_threads=2", StringComparison.Ordinal) && evidence.Contains("Seccomp:\t2", StringComparison.Ordinal), "options or process policy missing");
            }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
        });
}
tests.Complete();
