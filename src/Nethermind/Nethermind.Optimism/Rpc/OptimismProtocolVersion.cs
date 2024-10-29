// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;

namespace Nethermind.Optimism.Rpc;

public interface IOptimismProtocolVersion : IComparable<IOptimismProtocolVersion>
{
    public static IOptimismProtocolVersion Read(ReadOnlySpan<byte> span)
    {
        if (span.Length != 32) throw new OptimismProtocolVersionParseException($"Expected 32 bytes, got {span.Length}");

        var version = span[0];
        return version switch
        {
            0 => OptimismProtocolVersionV0.Read(span[1..]),
            _ => throw new OptimismProtocolVersionParseException($"Unsupported version {version}")
        };
    }
}

public sealed record OptimismSuperchainSignal
{
    public IOptimismProtocolVersion Recommended { get; }
    public IOptimismProtocolVersion Required { get; }

    public OptimismSuperchainSignal(IOptimismProtocolVersion recommended, IOptimismProtocolVersion required)
    {
        Recommended = recommended;
        Required = required;
    }
}

public interface IOptimismSuperchainSignalHandler
{
    IOptimismProtocolVersion CurrentVersion { get; }

    Task OnBehindRecommended();
    Task OnBehindRequired();
}

public class OptimismProtocolVersionParseException(string message) : Exception(message) { }

public sealed class OptimismProtocolVersionV0 : IOptimismProtocolVersion
{
    public byte[] Build { get; }
    public UInt32 Major { get; }
    public UInt32 Minor { get; }
    public UInt32 Patch { get; }
    public UInt32 PreRelease { get; }

    public OptimismProtocolVersionV0(ReadOnlySpan<byte> build, UInt32 major, UInt32 minor, UInt32 patch, UInt32 preRelease)
    {
        Build = build.ToArray();
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static OptimismProtocolVersionV0 Read(ReadOnlySpan<byte> span)
    {
        var reserved = span.TakeAndMove(7);
        if (!reserved.IsZero()) throw new OptimismProtocolVersionParseException("Expected reserved bytes to be zero");

        var build = span.TakeAndMove(8);
        var major = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var minor = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var patch = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
        var preRelease = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));

        return new OptimismProtocolVersionV0(build, major, minor, patch, preRelease);
    }

    public void Write(Span<byte> span)
    {
        span.TakeAndMove(7);

        Build.CopyTo(span.TakeAndMove(8));
        BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Major);
        BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Minor);
        BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Patch);
        BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), PreRelease);
    }

    public int CompareTo(IOptimismProtocolVersion? other)
    {
        if (ReferenceEquals(this, other)) return 0;

        if (other is null) return 1;

        if (other is not OptimismProtocolVersionV0 otherVersion)
        {
            throw new ArgumentException("Object is not a valid OptimismProtocolVersionV0", nameof(other));
        }

        var majorComparison = Major.CompareTo(otherVersion.Major);
        if (majorComparison != 0) return majorComparison;

        var minorComparison = Minor.CompareTo(otherVersion.Minor);
        if (minorComparison != 0) return minorComparison;

        var patchComparison = Patch.CompareTo(otherVersion.Patch);
        if (patchComparison != 0) return patchComparison;

        return (PreRelease, otherVersion.PreRelease) switch
        {
            (0, 0) => 0,
            (0, _) => 1,
            (_, 0) => -1,
            _ => PreRelease.CompareTo(otherVersion.PreRelease)
        };
    }

    private bool Equals(OptimismProtocolVersionV0 other) =>
        Build.SequenceEqual(other.Build)
            && Major == other.Major
            && Minor == other.Minor
            && Patch == other.Patch
            && PreRelease == other.PreRelease;

    public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is OptimismProtocolVersionV0 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Build, Major, Minor, Patch, PreRelease);
}
