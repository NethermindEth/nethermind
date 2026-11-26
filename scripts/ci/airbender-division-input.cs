// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

var max = (BigInteger.One << 256) - 1;
(BigInteger Quotient, BigInteger Remainder)[] hints =
[
    (4, 3), (5, 3), (4, 2),              // valid, wrong quotient, wrong remainder
    (BigInteger.One << 255, 0),          // product wraps when multiplied by two
    (max / 3, 1),                        // addition wraps after an exact product
    (3, 8), (4, 3)                       // noncanonical remainder, then a valid hint
];

// read_input expects a trusted header and consecutive state and code lists.
// Each list has one one-byte item with a 24-byte prefix, and code has 34 padding bytes.
const int nodes = 88, codes = nodes + 8 + 24 + 8, total = codes + 8 + 24 + 40;
var input = new byte[total + 8 + hints.Length * 2 * 64];
Set(0, 0x4E49544E);
Set(4, 1);
Set(64, nodes);
Set(68, codes);
Set(72, total);
Set(nodes, 1);
Set(nodes + 8, 1);
Set(codes, 1);
Set(codes + 8, 1);
Set(total, (uint)hints.Length * 2);
var offset = total + 8;
foreach (var (q, r) in hints)
{
    for (var i = 0; i < 2; i++)
    {
        q.ToByteArray(isUnsigned: true).CopyTo(input, offset);
        r.ToByteArray(isUnsigned: true).CopyTo(input, offset + 32);
        offset += 64;
    }
}

using var output = File.CreateText(args[0]);
output.Write(input.Length.ToString("x8", CultureInfo.InvariantCulture));
for (var i = 0; i < input.Length; i += 4)
    output.Write(BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(i)).ToString("x8", CultureInfo.InvariantCulture));

void Set(int position, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(position), value);
