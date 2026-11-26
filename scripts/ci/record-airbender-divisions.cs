// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false

using System.Buffers.Binary;
using System.Diagnostics;

var guest = Path.GetFullPath(args[0]);
var name = args[1];
if (Path.GetExtension(name) != ".nti" || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
    throw new ArgumentException("Expected a framed .nti fixture in the guest bin directory.");
var bin = Path.Combine(guest, "bin");
var input = File.ReadAllBytes(Path.Combine(bin, name));
var size = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(input));
if (size < 88 || size > input.Length - 8 || BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8)) != 0x4E49544E
    || BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8 + 72)) != size)
    throw new InvalidDataException("Expected a trusted input without an existing hint tape.");

var output = Path.Combine(bin, Path.GetFileNameWithoutExtension(name) + "-divisions.nti");
var temporary = Path.GetRandomFileName() + ".nti";
var candidate = Path.Combine(bin, temporary);
try
{
    using var tape = new MemoryStream();
    var expected = Run(name, tape, out var originalHits);
    if (originalHits != 0) throw new InvalidDataException("The recording input already contains hints.");
    var count = checked((int)(tape.Length / 64));
    var length = checked(size + 8 + (int)tape.Length);
    using (var stream = File.Create(candidate))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write((ulong)length);
        writer.Write(input, 8, size);
        writer.Write((uint)count);
        writer.Write(0u);
        tape.WriteTo(stream);
    }

    var actual = Run(temporary, null, out var hits);
    if (hits != count || actual != expected)
        throw new InvalidDataException($"Replay accepted {hits} of {count} division hints. Expected {expected}, got {actual}.");
    File.Move(candidate, output, overwrite: true);
    Console.WriteLine($"{output}: replay verified all {count} divisions and the public result.");
}
finally
{
    File.Delete(candidate);
    File.Delete(candidate + ".hex");
}

string Run(string fixture, Stream? tape, out int hits)
{
    hits = 0;
    string? result = null;
    var validated = false;
    var unexpectedRecord = false;
    var start = new ProcessStartInfo("make") { WorkingDirectory = guest, RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var argument in new[] { "run", "GUEST_NAME=division-recorder", $"INPUT={fixture}" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    while (process.StandardOutput.ReadLine() is { } line)
    {
        var message = line.StartsWith("UART: `", StringComparison.Ordinal) ? line[7..] : line;
        message = message.TrimEnd('`');
        if (message.StartsWith("DIV:", StringComparison.Ordinal))
        {
            if (tape is null || message.Length != 132) unexpectedRecord = true;
            else tape.Write(Convert.FromHexString(message.AsSpan(4)));
        }
        else if (message == "DIV-HIT") hits++;
        else if (message.Length == 86 && message.All(char.IsAsciiHexDigit)) validated = message.AsSpan(64, 2).SequenceEqual("01");
        else if (line.StartsWith("Result: ", StringComparison.Ordinal)) result = line;
    }
    process.WaitForExit();
    if (process.ExitCode != 0 || result is null || !validated || unexpectedRecord)
        throw new InvalidOperationException($"Airbender failed to validate {fixture} with the expected division stream.");
    return result;
}
