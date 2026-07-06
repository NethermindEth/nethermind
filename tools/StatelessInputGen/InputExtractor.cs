// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Spectre.Console;

namespace Nethermind.StatelessInputGen;

internal static class InputExtractor
{
    private const string BlocksProperty = "blocks";
    private const string InputBytesProperty = "statelessInputBytes";
    /// <summary>The cap on output file name length, keeping names manageable regardless of the output path.</summary>
    private const int MaxFileNameLength = 128;

    private static readonly char[] s_invalidFileNameChars = Path.GetInvalidFileNameChars();

    internal static async Task<int> ExtractFromFixtures(string fixturePath, string outputPath, bool forZisk, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string[] fixtureFiles;

        if (File.Exists(fixturePath))
        {
            fixtureFiles = [fixturePath];
        }
        else if (Directory.Exists(fixturePath))
        {
            fixtureFiles = Directory.GetFiles(fixturePath, "*.json", new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.None,
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            });
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Path not found: {fixturePath.EscapeMarkup()}[/]");
            return 1;
        }

        if (fixtureFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .json fixture files found in {fixturePath.EscapeMarkup()}[/]");
            return 1;
        }

        string fullOutputPath = Directory.CreateDirectory(outputPath).FullName;
        // 259 = Windows MAX_PATH (260) minus the string terminator; 1 more for the path separator
        int maxFileNameLength = OperatingSystem.IsWindows()
            ? Math.Min(MaxFileNameLength, 259 - fullOutputPath.Length - 1)
            : MaxFileNameLength;

        int extracted = 0;
        int failed = 0;
        HashSet<string> writtenFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string fixtureFile in fixtureFiles)
        {
            try
            {
                (int fileExtracted, int fileFailed) = await ExtractFromFile(
                    fixtureFile, fullOutputPath, forZisk, maxFileNameLength, writtenFiles, cancellationToken);

                extracted += fileExtracted;
                failed += fileFailed;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                failed++;
                AnsiConsole.MarkupLine($"[red]✗[/] {fixtureFile.EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
            }
        }

        AnsiConsole.MarkupLine(extracted == 0
            ? "[yellow]No stateless input found[/]"
            : $"[green]✓[/] Extracted {extracted} input(s) to [dim]{fullOutputPath.EscapeMarkup()}[/]");

        if (failed > 0)
            AnsiConsole.MarkupLine($"[red]✗[/] {failed} error(s) occurred");

        return failed == 0 ? 0 : 1;
    }

    private static async Task<(int Extracted, int Failed)> ExtractFromFile(
        string fixtureFile,
        string outputPath,
        bool forZisk,
        int maxFileNameLength,
        HashSet<string> writtenFiles,
        CancellationToken cancellationToken)
    {
        int extracted = 0;
        int failed = 0;

        await using FileStream stream = File.OpenRead(fixtureFile);
        using JsonDocument fixture = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (fixture.RootElement.ValueKind != JsonValueKind.Object)
            return (extracted, failed);

        foreach (JsonProperty test in fixture.RootElement.EnumerateObject())
        {
            if (test.Value.ValueKind != JsonValueKind.Object ||
                !test.Value.TryGetProperty(BlocksProperty, out JsonElement blocks) ||
                blocks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            int blockCount = blocks.GetArrayLength();
            int blockIndex = -1;

            foreach (JsonElement block in blocks.EnumerateArray())
            {
                blockIndex++;

                if (block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty(InputBytesProperty, out JsonElement inputBytes) ||
                    inputBytes.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                try
                {
                    string fileName = GetOutputFileName(test.Name, blockIndex, blockCount, maxFileNameLength);

                    if (!writtenFiles.Add(fileName))
                    {
                        failed++;
                        AnsiConsole.MarkupLine(
                            $"[red]✗[/] `{test.Name.EscapeMarkup()}`, block {blockIndex}: input skipped as its output file name is already taken by another test");
                        continue;
                    }

                    (byte[] buffer, int dataLength) = DecodeInput(inputBytes);

                    try
                    {
                        ReadOnlyMemory<byte> output = forZisk
                            ? buffer.AsMemory(0, ZiskFrame.FrameInPlace(buffer, dataLength))
                            : buffer.AsMemory(ZiskFrame.HeaderLength, dataLength);

                        await File.WriteAllBytesAsync(Path.Join(outputPath, fileName), output, cancellationToken);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    AnsiConsole.MarkupLine($"[green]✓[/] Saved [dim]{fileName.EscapeMarkup()}[/]");

                    extracted++;
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    failed++;
                    AnsiConsole.MarkupLine($"[red]✗[/] `{test.Name.EscapeMarkup()}`, block {blockIndex}: {ex.Message.EscapeMarkup()}");
                }
            }
        }

        return (extracted, failed);
    }

    /// <summary>
    /// Decodes the hex payload into a pooled buffer at offset <see cref="ZiskFrame.HeaderLength"/>,
    /// leaving room for in-place Zisk framing. The caller returns the buffer to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    /// <exception cref="FormatException">The payload is not a 0x-prefixed non-empty even-length hex string.</exception>
    private static (byte[] Buffer, int DataLength) DecodeInput(JsonElement inputBytes)
    {
        // Raw UTF-8 avoids materializing multi-MB hex payloads as UTF-16 strings; [1..^1] trims the JSON quotes.
        // JSON escapes cannot occur in valid hex, and any `\` fails the hex decode loudly.
        ReadOnlySpan<byte> hex = JsonMarshal.GetRawUtf8Value(inputBytes)[1..^1];

        if (!hex.StartsWith("0x"u8))
            throw new FormatException($"{InputBytesProperty} must be 0x-prefixed");

        hex = hex[2..];

        if (hex.Length == 0 || hex.Length % 2 != 0)
            throw new FormatException($"{InputBytesProperty} must be a non-empty even-length hex string");

        int dataLength = hex.Length / 2;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ZiskFrame.GetFrameLength(dataLength));

        try
        {
            Bytes.FromUtf8HexString(hex, buffer.AsSpan(ZiskFrame.HeaderLength, dataLength));
        }
        catch (FormatException)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw new FormatException($"{InputBytesProperty} is not valid hex");
        }

        return (buffer, dataLength);
    }

    private static string GetOutputFileName(string testName, int blockIndex, int blockCount, int maxLength)
    {
        const string extension = ".ssz";
        const int hashLength = 8;

        string name = string.Join('_', testName.Split(s_invalidFileNameChars));
        string suffix = blockCount > 1 ? $"_block_{blockIndex}" : string.Empty;
        // The floor keeps a hash-only name representable even for very deep output directories
        int maxNameLength = Math.Max(maxLength - suffix.Length - extension.Length, hashLength + 1);

        // A hash of the full test name keeps names unique when sanitization collapses
        // distinct punctuation into `_` or truncation drops the distinguishing tail
        if (name != testName || name.Length > maxNameLength)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(testName)))[..hashLength];

            if (name.Length > maxNameLength - hashLength - 1)
                name = name[..(maxNameLength - hashLength - 1)];

            name = $"{name}-{hash}";
        }

        return $"{name}{suffix}{extension}";
    }
}
