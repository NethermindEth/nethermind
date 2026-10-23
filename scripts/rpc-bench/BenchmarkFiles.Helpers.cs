// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class BenchmarkFiles
{
    internal static async Task<int> RunCombinedToFileAsync(string executable, IEnumerable<string> arguments, string output, CancellationToken cancellationToken, bool echo = false)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var console = echo ? Console.OpenStandardOutput() : Stream.Null;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
        using var gate = new SemaphoreSlim(1);
        var stdout = Copy(process.StandardOutput.BaseStream);
        var stderr = Copy(process.StandardError.BaseStream);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException exception) { Console.Error.WriteLine($"Child exited during cleanup: {exception.Message}"); }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        async Task Copy(Stream source)
        {
            var buffer = new byte[81920];
            while (await source.ReadAsync(buffer, cancellationToken) is var count && count > 0)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    if (echo) await console.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                catch (IOException)
                {
                    if (!process.HasExited) process.Kill(true);
                    throw;
                }
                finally { gate.Release(); }
            }
        }
    }

    internal static async Task FingerprintAsync(string directory, string output, CancellationToken cancellationToken)
    {
        var temporary = Directory.CreateTempSubdirectory("rpc-fingerprint-");
        try
        {
            var listing = Path.Combine(temporary.FullName, "listing");
            var sorted = Path.Combine(temporary.FullName, "sorted");
            await RunToFileAsync("find", [directory, "(", "-type", "f", "-o", "-type", "d", "-o", "-type", "l", ")", "-printf", "%P\t%y\t%s\t%T@\t%m\t%U:%G\t%l\n"], listing, true, cancellationToken);
            await RunToFileAsync("env", ["LC_ALL=C", "sort", listing], sorted, true, cancellationToken);
            await using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            await target.WriteAsync(Encoding.UTF8.GetBytes("# rpc-bench fingerprint v2\n# listing (path<TAB>type<TAB>size<TAB>mtime<TAB>mode<TAB>owner<TAB>linktarget)\n"), cancellationToken);
            await using (var source = File.OpenRead(sorted)) await source.CopyToAsync(target, cancellationToken);
            await target.WriteAsync(Encoding.UTF8.GetBytes("# control-file-hashes\n"), cancellationToken);
            var controls = await BenchmarkProcess.RunAsync("find", [directory, "-type", "f", "(", "-name", "CURRENT", "-o", "-name", "IDENTITY", "-o", "-name", "MANIFEST-*", "-o", "-name", "OPTIONS-*", ")", "-print0"], cancellationToken);
            if (controls.Code != 0 || controls.Error.Length > 0) throw new IOException($"db_fingerprint: find reported errors for '{directory}' — refusing to produce a partial fingerprint");
            foreach (var file in controls.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
            {
                await using var stream = File.OpenRead(file);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                await target.WriteAsync(Encoding.UTF8.GetBytes($"{hash}  {Path.GetRelativePath(directory, file)}\n"), cancellationToken);
            }
        }
        finally { temporary.Delete(recursive: true); }
    }

    internal static async Task RunToFileAsync(string executable, IEnumerable<string> arguments, string output, bool rejectErrorOutput, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}");
        var copy = Copy();
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await copy;
            var error = await errors;
            if (process.ExitCode != 0 || (rejectErrorOutput && error.Length > 0)) throw new IOException($"{executable} failed: {error.Trim()}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException exception) { Console.Error.WriteLine($"Child exited during cleanup: {exception.Message}"); }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(copy, errors); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        async Task Copy()
        {
            try { await process.StandardOutput.BaseStream.CopyToAsync(stream, cancellationToken); }
            catch (IOException)
            {
                if (!process.HasExited) process.Kill(true);
                throw;
            }
        }
    }
}
