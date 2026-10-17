// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

[Parallelizable(ParallelScope.All)]
public class Dynamics
{
    [Test]
    public void String()
    {
        AbiSignature<string> signature = new AbiSignature("f")
            .With(
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("91e145ef");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, "hello");

        byte[] expected = Bytes.FromHexString(
            "91e145ef" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Location of the data part of the first parameter
            "0000000000000000000000000000000000000000000000000000000000000005" + // Data part of the first argument
            "68656c6c6f000000000000000000000000000000000000000000000000000000"); // Contents of the first argument, `"hello"`

        Assert.That(encoded, Is.EqualTo(expected));

        string decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo("hello"));
    }

    [Test]
    public void PairOfStrings()
    {
        AbiSignature<(string, string)> signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("0x18159cfb");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "world"));

        byte[] expected = Bytes.FromHexString(
            "0x18159cfb" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of second string
            "776f726c64000000000000000000000000000000000000000000000000000000"); // "world"

        Assert.That(encoded, Is.EqualTo(expected));

        (string, string) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(("hello", "world")));
    }

    [Test]
    public void ThreeStrings()
    {
        AbiSignature<(string, string, string)> signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String,
                AbiType.String
            );

        byte[] expectedMethodId = Bytes.FromHexString("e18744f7");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ("hello", "my", "friends"));

        byte[] expected = Bytes.FromHexString(
            "e18744f7" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first string
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to second string
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of second string
            "6d79000000000000000000000000000000000000000000000000000000000000" + // "my"
            "0000000000000000000000000000000000000000000000000000000000000007" + // Length of third string
            "667269656e647300000000000000000000000000000000000000000000000000");  // "friends"

        Assert.That(encoded, Is.EqualTo(expected));

        (string, string, string) decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(("hello", "my", "friends")));
    }

    [Test]
    public void ArrayOfBools()
    {
        AbiSignature<bool[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Bool)
            );

        byte[] expectedMethodId = Bytes.FromHexString("da095a04");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, [true, false, true]);

        byte[] expected = Bytes.FromHexString(
            "da095a04" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element, `true`
            "0000000000000000000000000000000000000000000000000000000000000000" + // Second element, `false`
            "0000000000000000000000000000000000000000000000000000000000000001"); // Third element, `true`

        Assert.That(encoded, Is.EqualTo(expected));

        bool[] decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(new[] { true, false, true }));
    }

    [Test]
    public void ArrayOfStrings()
    {
        AbiSignature<string[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.String)
            );

        byte[] expectedMethodId = Bytes.FromHexString("e9cc8780");
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(expectedMethodId));

        byte[] encoded = AbiCodec.Encode(signature, ["hello", "my", "friends"]);

        byte[] expected = Bytes.FromHexString(
            "e9cc8780" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000003" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first string
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to second string
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Data part of the first argument
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // Contents of the first argument, "hello"
            "0000000000000000000000000000000000000000000000000000000000000002" + // Data part of the second argument
            "6d79000000000000000000000000000000000000000000000000000000000000" + // Contents of the second argument, "my"
            "0000000000000000000000000000000000000000000000000000000000000007" + // Data part of the third argument
            "667269656e647300000000000000000000000000000000000000000000000000"); // Contents of the third argument, "friends"

        Assert.That(encoded, Is.EqualTo(expected));

        string[] decoded = AbiCodec.Decode(signature, encoded);
        Assert.That(decoded, Is.EqualTo(new[] { "hello", "my", "friends" }));
    }

    [Test]
    public void DynamicTuple()
    {
        AbiSignature<(UInt256, string)> signature = new AbiSignature("f")
            .With(
                AbiType.Tuple(AbiType.UInt256, AbiType.String)
            );

        (UInt256, string) arguments = (1, "a");
        AssertRoundTrip(signature, "c260125c", arguments,
            "c260125c" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component, relative to the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component
            "6100000000000000000000000000000000000000000000000000000000000000"); // "a"
    }

    [Test]
    public void ArrayOfDynamicTuples()
    {
        AbiSignature<(UInt256, string)[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Tuple(AbiType.UInt256, AbiType.String))
            );

        (UInt256, string)[] arguments = [(1, "a"), (2, "b")];
        AssertRoundTrip(signature, "7f9296cc", arguments,
            "7f9296cc" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "00000000000000000000000000000000000000000000000000000000000000c0" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of first element, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000002" + // First component of second element, `2`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of second element
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"
    }

    [Test]
    public void DynamicTupleBetweenArguments()
    {
        AbiSignature<(UInt256, (UInt256, string), string)> signature = new AbiSignature("f")
            .With(
                AbiType.UInt256,
                AbiType.Tuple(AbiType.UInt256, AbiType.String),
                AbiType.String
            );

        (UInt256, (UInt256, string), string) arguments = (2, (1, "a"), "b");
        AssertRoundTrip(signature, "004b756e", arguments,
            "004b756e" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000002" + // First argument, `2`
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to the tuple
            "00000000000000000000000000000000000000000000000000000000000000e0" + // Offset to third argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of the tuple
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third argument
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"
    }

    [Test]
    public void NestedDynamicTuple()
    {
        AbiSignature<(string, (UInt256, string))> signature = new AbiSignature("f")
            .With(
                AbiType.Tuple(AbiType.String, AbiType.Tuple(AbiType.UInt256, AbiType.String))
            );

        (string, (UInt256, string)) arguments = ("b", (1, "a"));
        AssertRoundTrip(signature, "e3b34b9a", arguments,
            "e3b34b9a" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the outer tuple
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first component of the outer tuple
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to the inner tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first component of the outer tuple
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the inner tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the inner tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second component of the inner tuple
            "6100000000000000000000000000000000000000000000000000000000000000"); // "a"
    }

    [Test]
    public void ArrayOfTuplesWithSeveralTails()
    {
        AbiSignature<(string, UInt256, string)[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Tuple(AbiType.String, AbiType.UInt256, AbiType.String))
            );

        (string, UInt256, string)[] arguments = [("a", 1, "b"), ("c", 2, "d")];
        AssertRoundTrip(signature, "689595a5", arguments,
            "689595a5" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000120" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Second component of first element, `1`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to third component of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first component of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third component of first element
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000060" + // Offset to first component of second element
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second component of second element, `2`
            "00000000000000000000000000000000000000000000000000000000000000a0" + // Offset to third component of second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first component of second element
            "6300000000000000000000000000000000000000000000000000000000000000" + // "c"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of third component of second element
            "6400000000000000000000000000000000000000000000000000000000000000"); // "d"
    }

    [Test]
    public void DynamicFixedArray()
    {
        AbiSignature<string[]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.String, 2)
            );

        string[] arguments = ["a", "b"];
        AssertRoundTrip(signature, "d72d3724", arguments,
            "d72d3724" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element, relative to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second element, relative to the fixed array
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second element
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"
    }

    [Test]
    public void ArrayOfDynamicFixedArrays()
    {
        AbiSignature<string[][]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Array(AbiType.String, 2))
            );

        string[][] arguments = [["a", "b"], ["c", "d"]];
        AssertRoundTrip(signature, "10211b6b", arguments,
            "10211b6b" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first element
            "0000000000000000000000000000000000000000000000000000000000000100" + // Offset to second element
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of first element
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of first element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of first element
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of first element
            "6200000000000000000000000000000000000000000000000000000000000000" + // "b"
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of second element
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of second element
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of second element
            "6300000000000000000000000000000000000000000000000000000000000000" + // "c"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of second element
            "6400000000000000000000000000000000000000000000000000000000000000"); // "d"
    }

    [Test]
    public void DynamicFixedArrayInTuple()
    {
        AbiSignature<(UInt256, string[])> signature = new AbiSignature("f")
            .With(
                AbiType.Tuple(AbiType.UInt256, AbiType.Array(AbiType.String, 2))
            );

        (UInt256, string[]) arguments = (1, ["a", "b"]);
        AssertRoundTrip(signature, "bfb8b830", arguments,
            "bfb8b830" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the tuple
            "0000000000000000000000000000000000000000000000000000000000000001" + // First component of the tuple, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second component of the tuple
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string of second component
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string of second component
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of first string of second component
            "6100000000000000000000000000000000000000000000000000000000000000" + // "a"
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second string of second component
            "6200000000000000000000000000000000000000000000000000000000000000"); // "b"
    }

    [Test]
    public void ArrayOfStaticFixedArrays()
    {
        AbiSignature<UInt256[][]> signature = new AbiSignature("f")
            .With(
                AbiType.Array(AbiType.Array(AbiType.UInt256, 2))
            );

        UInt256[][] arguments = [[1, 2], [3, 4]];
        AssertRoundTrip(signature, "047d9e3a", arguments,
            "047d9e3a" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000020" + // Offset to the start of the array
            "0000000000000000000000000000000000000000000000000000000000000002" + // Length of the array
            "0000000000000000000000000000000000000000000000000000000000000001" + // First element of first fixed array, `1`
            "0000000000000000000000000000000000000000000000000000000000000002" + // Second element of first fixed array, `2`
            "0000000000000000000000000000000000000000000000000000000000000003" + // First element of second fixed array, `3`
            "0000000000000000000000000000000000000000000000000000000000000004"); // Second element of second fixed array, `4`
    }

    [Test]
    public void DynamicTypeDeclaringWrongHeadSize()
    {
        Abi<string> wideString = new()
        {
            Name = AbiType.String.Name,
            Read = AbiType.String.Read,
            Write = AbiType.String.Write,
            HeadSize = 64,
            TailSize = AbiType.String.TailSize
        };
        AbiSignature<(UInt256, string)> signature = new AbiSignature("f")
            .With(
                AbiType.UInt256,
                wideString
            );

        (UInt256, string) arguments = (1, "a");
        AssertRoundTrip(signature, "1ba84e08", arguments,
            "1ba84e08" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000001" + // First argument, `1`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to second argument
            "0000000000000000000000000000000000000000000000000000000000000001" + // Length of second argument
            "6100000000000000000000000000000000000000000000000000000000000000"); // "a"
    }

    private static void AssertRoundTrip<T>(AbiSignature<T> signature, string expectedMethodId, T value, string expectedEncoding)
    {
        Assert.That(signature.MethodId.ToArray(), Is.EqualTo(Bytes.FromHexString(expectedMethodId)));

        byte[] encoded = AbiCodec.Encode(signature, value);
        Assert.That(encoded, Is.EqualTo(Bytes.FromHexString(expectedEncoding)));

        Assert.That(AbiCodec.Decode(signature, encoded), Is.EqualTo(value));
    }
}
