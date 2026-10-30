// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Stateless.Execution.IO;
using Spectre.Console;

namespace Nethermind.StatelessInputGen;

internal static class InputConverter
{
    /// <summary>Converts each stateless input (.ssz) at <paramref name="source"/> to a trusted input (.nti) in
    /// <paramref name="output"/>, printing the result and its keccak a trusted guest validating it reports.</summary>
    /// <param name="zisk">Whether the inputs are, and the outputs are to be, in the Zisk input frame.</param>
    internal static async Task<int> Convert(string source, string output, bool zisk, CancellationToken cancellationToken = default)
    {
        string[] files = File.Exists(source) ? [source] : Directory.GetFiles(source, "*.ssz");
        if (files.Length == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .ssz input found at {source.EscapeMarkup()}[/]");
            return 1;
        }

        Directory.CreateDirectory(output);
        foreach (string file in files)
        {
            byte[] data = await File.ReadAllBytesAsync(file, cancellationToken);
            if (zisk)
            {
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(data);
                if (length > (ulong)(data.Length - ZiskFrame.HeaderLength))
                    throw new InvalidDataException($"{file}: the framed length {length} exceeds the {data.Length}-byte file");

                data = data.AsSpan(ZiskFrame.HeaderLength, (int)length).ToArray();
            }

            byte[] trusted = TrustedInput.FromStatelessInput(data);
            byte[] result = StatelessValidationResult.Encode(new StatelessValidationResult
            {
                NewPayloadRequestRoot = Keccak.Compute(TrustedInput.BlockHeaderOf(trusted)),
                IsSuccess = true,
                ChainId = TrustedInput.ChainIdOf(trusted),
                SchemaId = TrustedInput.SchemaId
            });

            string name = Path.ChangeExtension(Path.GetFileName(file), ".nti");
            await File.WriteAllBytesAsync(Path.Join(output, name), zisk ? ZiskFrame.Wrap(trusted) : trusted, cancellationToken);

            // Plain output a workflow can read: the file, the result a guest validating it reports, and its keccak.
            Console.WriteLine($"{name} {System.Convert.ToHexStringLower(result)} {Keccak.Compute(result).ToString(withZeroX: false)}");
        }

        return 0;
    }
}
