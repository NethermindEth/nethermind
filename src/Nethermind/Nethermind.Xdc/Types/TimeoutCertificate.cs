// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Xdc.Types;

public class TimeoutCertificate(ulong round, Signature[] signatures, ulong gapNumber) : IEquatable<TimeoutCertificate>
{
    public ulong Round { get; set; } = round;
    public Signature[] Signatures { get; set; } = signatures;
    public ulong GapNumber { get; set; } = gapNumber;

    public bool Equals(TimeoutCertificate? other) =>
        other is not null &&
        Round == other.Round &&
        Signatures.AsSpan().SequenceEqual(other.Signatures) &&
        GapNumber == other.GapNumber;

    public override bool Equals(object? obj) => Equals(obj as TimeoutCertificate);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(Round);
        AddSignaturesHashCode(ref hashCode, Signatures);
        hashCode.Add(GapNumber);
        return hashCode.ToHashCode();
    }

    private static void AddSignaturesHashCode(ref HashCode hashCode, Signature[]? signatures)
    {
        if (signatures is null)
        {
            hashCode.Add(0);
            return;
        }

        hashCode.Add(signatures.Length);
        for (int i = 0; i < signatures.Length; i++)
        {
            hashCode.Add(signatures[i]);
        }
    }
}
