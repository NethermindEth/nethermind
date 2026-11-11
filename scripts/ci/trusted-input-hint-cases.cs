// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false

using System.Buffers.Binary;

var input = File.ReadAllBytes(args[0]);
var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(input));
if (length < 88 || length > input.Length - 8 || BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8)) != 0x4E49544E)
    throw new InvalidDataException("Expected a framed trusted input.");

var senderOffset = Read(48);
var senderCount = Read(52) / 96;
var recoveryOffset = Read(80);
var recoveryCount = Read(84) / 193;
if (senderCount == 0) throw new InvalidDataException("The fixture must exercise sender hint verification.");

WriteCase("sender-key", senderOffset);
WriteCase("sender-y", senderOffset + senderCount * 64);
if (recoveryCount > 0)
{
    WriteCase("recovery-message", recoveryOffset);
    WriteCase("recovery-signature", recoveryOffset + recoveryCount * 32);
    WriteCase("recovery-key", recoveryOffset + recoveryCount * 96);
    WriteCase("recovery-y", recoveryOffset + recoveryCount * 160);
    WriteCase("recovery-id", recoveryOffset + recoveryCount * 192);
}

int Read(int offset) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(8 + offset)));

void WriteCase(string name, int offset)
{
    if (offset < 88 || offset >= length) throw new InvalidDataException("Hint offset is outside the input.");
    var mutated = (byte[])input.Clone();
    mutated[8 + offset] ^= 1;
    var path = Path.Combine(Path.GetDirectoryName(args[0])!, $"{Path.GetFileNameWithoutExtension(args[0])}-{name}.nti");
    File.WriteAllBytes(path, mutated);
    Console.WriteLine(Path.GetFileName(path));
}
