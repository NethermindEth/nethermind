// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Optimism.ProtocolVersion;

[JsonConverter(typeof(JsonConverter))]
public abstract class OptimismProtocolVersion : IEquatable<OptimismProtocolVersion>, IComparable<OptimismProtocolVersion>
{
    public const int ByteLength = 32;

    public class ParseException(string message) : Exception(message);

    private OptimismProtocolVersion() { }

    public static OptimismProtocolVersion Read(ReadOnlySpan<byte> span)
    {
        if (span.Length < ByteLength) throw new ParseException($"Expected at least {ByteLength} bytes");

        var version = span[0];
        return version switch
        {
            0 => V0.Read(span),
            _ => throw new ParseException($"Unsupported version: {version}")
        };
    }

    public abstract void Write(Span<byte> span);

    public abstract int CompareTo(OptimismProtocolVersion? other);

    public abstract bool Equals(OptimismProtocolVersion? other);

    public override bool Equals(object? obj) => obj is OptimismProtocolVersion other && Equals(other);
    public abstract override int GetHashCode();

    public static bool operator >(OptimismProtocolVersion left, OptimismProtocolVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(OptimismProtocolVersion left, OptimismProtocolVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(OptimismProtocolVersion left, OptimismProtocolVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(OptimismProtocolVersion left, OptimismProtocolVersion right) => left.CompareTo(right) <= 0;
    public static bool operator ==(OptimismProtocolVersion left, OptimismProtocolVersion right) => left.Equals(right);
    public static bool operator !=(OptimismProtocolVersion left, OptimismProtocolVersion right) => !left.Equals(right);

    public abstract override string ToString();

    public sealed class V0 : OptimismProtocolVersion
    {
        public byte[] Build { get; }
        public uint Major { get; }
        public uint Minor { get; }
        public uint Patch { get; }
        public uint PreRelease { get; }

        public V0(ReadOnlySpan<byte> build, uint major, uint minor, uint patch, uint preRelease)
        {
            if (build.Length != 8) throw new ArgumentException($"Expected build identifier to be 8 bytes long, got {build.Length}", nameof(build));

            Build = build.ToArray();
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
        }

        public new static V0 Read(ReadOnlySpan<byte> span)
        {
            var version = span[0];
            if (version != 0) throw new ParseException($"Expected version 0, got {version}");

            var reserved = span.TakeAndMove(7);
            if (!reserved.IsZero()) throw new ParseException("Expected reserved bytes to be zero");

            var build = span.TakeAndMove(8);
            var major = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
            var minor = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
            var patch = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));
            var preRelease = BinaryPrimitives.ReadUInt32BigEndian(span.TakeAndMove(4));

            return new V0(build, major, minor, patch, preRelease);
        }

        public override void Write(Span<byte> span)
        {
            span[0] = 0;

            span.TakeAndMove(7);

            Build.CopyTo(span.TakeAndMove(8));
            BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Major);
            BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Minor);
            BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), Patch);
            BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(4), PreRelease);
        }

        public override int CompareTo(OptimismProtocolVersion? other)
        {
            if (ReferenceEquals(this, other)) return 0;

            if (other is null) return 1;

            if (other is not V0 otherVersion) throw new ArgumentException("Object is not a valid OptimismProtocolVersion.V0", nameof(other));

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

        public override bool Equals(OptimismProtocolVersion? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            if (other is not V0 otherVersion) return false;

            return Build.SequenceEqual(otherVersion.Build)
                   && Major == otherVersion.Major
                   && Minor == otherVersion.Minor
                   && Patch == otherVersion.Patch
                   && PreRelease == otherVersion.PreRelease;
        }

        public override bool Equals(object? obj) => ReferenceEquals(this, obj) || obj is V0 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Build, Major, Minor, Patch, PreRelease);

        public override string ToString() => $"v{Major}.{Minor}.{Patch}{(PreRelease == 0 ? "" : $"-{PreRelease}")}";
    }

    internal class JsonConverter : JsonConverter<OptimismProtocolVersion>
    {
        public override OptimismProtocolVersion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            ByteArrayConverter.Convert(ref reader, bytes);
            return OptimismProtocolVersion.Read(bytes);
        }

        public override void Write(Utf8JsonWriter writer, OptimismProtocolVersion value, JsonSerializerOptions options)
        {
            Span<byte> bytes = stackalloc byte[ByteLength];
            value.Write(bytes);
            ByteArrayConverter.Convert(writer, bytes, skipLeadingZeros: false);
        }
    }
}
