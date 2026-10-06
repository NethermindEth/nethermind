// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Core.Crypto;
using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1;

/// <summary>
/// Rebuilds or checks the <see cref="EraExporter.AccumulatorFileName"/> and <see cref="EraExporter.ChecksumsFileName"/>
/// manifests of existing era1 files without access to the chain that produced them.
/// </summary>
public static class EraManifestGenerator
{
    /// <summary>
    /// Writes the accumulator and checksum manifests for the given era1 files to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="eraPath">A single era1 file, or a directory whose top-level era1 files of <paramref name="network"/> are processed.</param>
    /// <param name="network">The network name that prefixes the era1 file names.</param>
    /// <param name="outputDirectory">The directory to write the manifests to. Existing manifests are overwritten.</param>
    /// <param name="fileSystem">The file system used to list era1 files and write the manifests.</param>
    /// <param name="cancellation">The cancellation token.</param>
    /// <exception cref="EraException">
    /// No era1 files were found, the files do not cover contiguous epochs, a file cannot be read,
    /// or a file's stored accumulator does not match its content or its name.
    /// </exception>
    public static async Task GenerateAsync(string eraPath, string network, string outputDirectory, IFileSystem fileSystem, CancellationToken cancellation = default)
    {
        string[] eraFiles = fileSystem.File.Exists(eraPath)
            ? [eraPath]
            : EraPathUtils.GetAllEraFiles(eraPath, network, fileSystem).ToArray();
        (string[] accumulators, string[] checksums) = await CalculateLines(eraPath, eraFiles, network, cancellation);

        fileSystem.Directory.CreateDirectory(outputDirectory);
        await fileSystem.File.WriteAllLinesAsync(Path.Combine(outputDirectory, EraExporter.AccumulatorFileName), accumulators, cancellation);
        await fileSystem.File.WriteAllLinesAsync(Path.Combine(outputDirectory, EraExporter.ChecksumsFileName), checksums, cancellation);
    }

    /// <summary>
    /// Checks that the manifests in <paramref name="directory"/> list the era1 files of <paramref name="network"/> in that directory, one line per epoch in epoch order.
    /// </summary>
    /// <param name="directory">The directory containing the era1 files and their manifests.</param>
    /// <param name="network">The network name that prefixes the era1 file names.</param>
    /// <param name="fileSystem">The file system used to list era1 files and read the manifests.</param>
    /// <param name="cancellation">The cancellation token.</param>
    /// <returns>A description of each missing, extra or mismatched manifest line; empty when the manifests match.</returns>
    /// <remarks>
    /// Lines are compared by position because <see cref="EraStore"/> looks up the checksum of an epoch by its offset from the first epoch.
    /// Only the hash column is compared, matching the importer; older exports can contain stale file names or just hashes.
    /// </remarks>
    /// <exception cref="EraException">The era1 files cannot be processed; see <see cref="GenerateAsync"/>.</exception>
    public static async Task<IReadOnlyList<string>> VerifyAsync(string directory, string network, IFileSystem fileSystem, CancellationToken cancellation = default)
    {
        string[] eraFiles = EraPathUtils.GetAllEraFiles(directory, network, fileSystem).ToArray();
        (string[] accumulators, string[] checksums) = await CalculateLines(directory, eraFiles, network, cancellation);

        List<string> mismatches = [];
        await CompareLines(EraExporter.AccumulatorFileName, accumulators);
        await CompareLines(EraExporter.ChecksumsFileName, checksums);
        return mismatches;

        async Task CompareLines(string manifestFileName, string[] expectedLines)
        {
            string manifestPath = Path.Combine(directory, manifestFileName);
            if (!fileSystem.File.Exists(manifestPath))
            {
                mismatches.Add($"{manifestPath} does not exist");
                return;
            }

            string[] actualLines = await fileSystem.File.ReadAllLinesAsync(manifestPath, cancellation);
            int lineCount = Math.Max(expectedLines.Length, actualLines.Length);
            for (int i = 0; i < lineCount; i++)
            {
                string? expected = i < expectedLines.Length ? expectedLines[i] : null;
                string? actual = i < actualLines.Length ? actualLines[i] : null;
                if (!HasSameHash(expected, actual))
                {
                    mismatches.Add($"{manifestFileName} line {i + 1}: expected \"{expected ?? "<none>"}\", found \"{actual ?? "<none>"}\"");
                }
            }
        }
    }

    private static bool HasSameHash(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        try
        {
            return EraPathUtils.ExtractHashFromAccumulatorAndCheckSumEntry(expected)
                == EraPathUtils.ExtractHashFromAccumulatorAndCheckSumEntry(actual);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<(string[] Accumulators, string[] Checksums)> CalculateLines(string eraPath, string[] eraFiles, string network, CancellationToken cancellation)
    {
        if (eraFiles.Length == 0) throw new EraException($"No {network} era1 files found at {eraPath}.");

        ulong[] epochs = new ulong[eraFiles.Length];
        for (int i = 0; i < eraFiles.Length; i++)
        {
            string[] parts = Path.GetFileName(eraFiles[i]).Split('-');
            if (parts.Length != 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(parts[1], out epochs[i]))
                throw new EraException($"{Path.GetFileName(eraFiles[i])} is not named as a {network} era1 file.");
        }

        Array.Sort(epochs, eraFiles);
        for (int i = 1; i < epochs.Length; i++)
        {
            if (epochs[i] == epochs[i - 1])
                throw new EraException($"Epoch {epochs[i]} has more than one era1 file: {Path.GetFileName(eraFiles[i - 1])}, {Path.GetFileName(eraFiles[i])}.");
            if (epochs[i] != epochs[i - 1] + 1)
                throw new EraException($"Epoch {epochs[i - 1] + 1} is missing. The era1 files must cover contiguous epochs.");
        }

        string[] accumulators = new string[eraFiles.Length];
        string[] checksums = new string[eraFiles.Length];
        await Parallel.ForAsync(0, eraFiles.Length, cancellation, async (i, cancel) =>
        {
            string fileName = Path.GetFileName(eraFiles[i]);
            (ValueHash256 accumulator, ValueHash256 calculatedAccumulator, ValueHash256 checksum) = await ReadEraFile(eraFiles[i], cancel);
            if (accumulator != calculatedAccumulator)
                throw new EraVerificationException($"Computed accumulator does not match stored accumulator in {fileName}.");

            // An interrupted export leaves its file under a temporary name with a zero root.
            string expectedFileName = EraPathUtils.Filename(network, epochs[i], new Hash256(accumulator));
            if (!expectedFileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                throw new EraVerificationException($"{fileName} does not match its accumulator {accumulator}. Expected the name {expectedFileName}.");

            accumulators[i] = $"{accumulator} {fileName}";
            checksums[i] = $"{checksum} {fileName}";
        });

        return (accumulators, checksums);
    }

    private static async Task<(ValueHash256 Accumulator, ValueHash256 CalculatedAccumulator, ValueHash256 Checksum)> ReadEraFile(string eraFile, CancellationToken cancellation)
    {
        try
        {
            using EraReader reader = new(eraFile);
            return (reader.ReadAccumulator(), await reader.CalculateAccumulator(cancellation), reader.CalculateChecksum());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new EraException($"Unable to read {Path.GetFileName(eraFile)}: {e.Message}");
        }
    }
}
