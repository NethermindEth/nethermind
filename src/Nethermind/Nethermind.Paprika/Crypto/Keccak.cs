using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Runtime.CompilerServices.Unsafe;

namespace Paprika.Crypto;

[DebuggerStepThrough]
[DebuggerDisplay("{ToString()}")]
public readonly struct Keccak : IEquatable<Keccak>
{
    private readonly Vector256<byte> Bytes;

    public const int Size = 32;

    public Span<byte> BytesAsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref AsRef(in Bytes), 1));

    public ReadOnlySpan<byte> Span =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref AsRef(in Bytes), 1));

    /// <returns>
    ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
    /// </returns>
    public static readonly Keccak OfAnEmptyString = InternalCompute(new byte[] { });

    /// <returns>
    ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
    /// </returns>
    public static readonly Keccak OfAnEmptySequenceRlp = InternalCompute(new byte[] { 192 });

    /// <summary>
    ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
    /// </summary>
    public static readonly Keccak EmptyTreeHash = InternalCompute(new byte[] { 128 });

    /// <returns>
    ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
    /// </returns>
    public static Keccak Zero => default;

    public Keccak(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            Bytes = OfAnEmptyString.Bytes;
            return;
        }

        Debug.Assert(bytes.Length == Size);
        Bytes = As<byte, Vector256<byte>>(ref MemoryMarshal.GetArrayDataReference(bytes));
    }

    public Keccak(Span<byte> bytes)
        : this((ReadOnlySpan<byte>)bytes)
    {
    }

    public Keccak(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            Bytes = OfAnEmptyString.Bytes;
            return;
        }

        Debug.Assert(bytes.Length == Size);
        Bytes = As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(bytes));
    }

    [DebuggerStepThrough]
    public static Keccak Compute(ReadOnlySpan<byte> input)
    {
        if (input.Length == 0)
        {
            return OfAnEmptyString;
        }

        KeccakHash.ComputeHash(input, out Keccak result);
        return result;
    }

    private static Keccak InternalCompute(byte[] input)
    {
        KeccakHash.ComputeHash(input, out Keccak result);
        return result;
    }

    public override bool Equals(object? obj) => obj is Keccak keccak && Equals(keccak);

    public bool Equals(Keccak other) => Bytes.Equals(other.Bytes);

    public override int GetHashCode()
    {
        var v0 = As<Vector256<byte>, long>(ref AsRef(in Bytes));
        var v1 = Add(ref As<Vector256<byte>, long>(ref AsRef(in Bytes)), 1);
        var v2 = Add(ref As<Vector256<byte>, long>(ref AsRef(in Bytes)), 2);
        var v3 = Add(ref As<Vector256<byte>, long>(ref AsRef(in Bytes)), 3);
        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return (int)v0 ^ (int)(v0 >> 32);
    }

    public ulong GetHashCodeUlong()
    {
        var v0 = As<Vector256<byte>, ulong>(ref AsRef(in Bytes));
        var v1 = Add(ref As<Vector256<byte>, ulong>(ref AsRef(in Bytes)), 1);
        var v2 = Add(ref As<Vector256<byte>, ulong>(ref AsRef(in Bytes)), 2);
        var v3 = Add(ref As<Vector256<byte>, ulong>(ref AsRef(in Bytes)), 3);
        v0 ^= v1;
        v2 ^= v3;
        v0 ^= v2;

        return v0;
    }

    public override string ToString() => ToString(true);

    public string ToString(bool withZeroX) => Span.ToHexString(withZeroX);

    public static bool operator ==(in Keccak left, in Keccak right) => left.Equals(right);

    public static bool operator !=(in Keccak left, in Keccak right) => !(left == right);
    public static Vector256<byte> operator ^(in Keccak left, in Keccak right)
    {
        return left.Bytes ^ right.Bytes;
    }
}
