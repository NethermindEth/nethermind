// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

static class Gcp
{
    internal static string InstanceName(string label, string attempt) => $"gh-{new string(label.ToLowerInvariant().Replace('_', '-').Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-').Take(40).ToArray())}-{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(label)))[..8]}-a{attempt}";
    internal static string Region(string zone) => zone.Contains('-') ? zone[..zone.LastIndexOf('-')] : zone;
    internal static string[] Zones(string zones) => zones.Split(',').Select(zone => Regex.Replace(zone, @"\s", "")).Where(zone => zone.Length > 0).ToArray();
    internal static string[] OrderZones(string seed, string zones)
    {
        var groups = Zones(zones).GroupBy(Region).Select(group => group.ToArray()).ToArray();
        var offset = ushort.Parse(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(seed)))[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Enumerable.Range(0, groups.Length).SelectMany(i =>
        {
            var group = groups[(i + offset) % groups.Length];
            return Enumerable.Range(0, group.Length).Select(j => group[(j + offset) % group.Length]);
        }).ToArray();
    }
    internal static string Classify(string error)
    {
        foreach (var (kind, pattern) in new[]
        {
            ("not-offered", "Machine type with name .* does not exist in zone"),
            ("quota", "Quota .* exceeded.* in region "),
            ("retryable", "ZONE_RESOURCE_POOL_EXHAUSTED|RESOURCE_POOL_EXHAUSTED|does not have enough resources|resource availability|currently unavailable|No available zone"),
            ("fatal", "PERMISSION_DENIED|Required .* permission|QUOTA_EXCEEDED|Quota .* exceeded")
        }) if (Regex.IsMatch(error, pattern)) return kind;
        return "unrecognised";
    }
    internal static async Task<CommandResult> Run(string executable, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            // Observe both readers even when cancellation interrupts the child.
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { Console.Error.WriteLine($"Cancelled {executable}"); }
        }
    }
}

record CommandResult(int Code, string Output = "", string Error = "");
