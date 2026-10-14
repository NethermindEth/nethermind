// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using SszLib = Nethermind.Serialization.Ssz.Ssz;

namespace Nethermind.Serialization.SszGenerator.Test;

[NonParallelizable]
public class CustomCollectionOwnershipTest
{
    public enum Failure { None, Constructor, LaterField }

    [Test]
    public void Decode_releases_each_element_once_with_declared_collection_ownership([Values] bool ownsElements, [Values] Failure failure)
    {
        SszOwnedUlongCollection.ResetCounts();
        CustomOwnedChildren.Reset(ownsElements, failure == Failure.Constructor);
        byte[] data = CreatePayload(failure != Failure.LaterField);

        if (ownsElements)
            AssertDecode<DeepOwnedDecodeParent>(data, failure == Failure.None, static parent => parent.Children);
        else
            AssertDecode<ShallowOwnedDecodeParent>(data, failure == Failure.None, static parent => parent.Children);

        using (Assert.EnterMultipleScope())
        {
            int expectedCollections = failure == Failure.Constructor ? 0 : 1;
            Assert.That(CustomOwnedChildren.CreatedCount, Is.EqualTo(expectedCollections));
            Assert.That(CustomOwnedChildren.DisposedCount, Is.EqualTo(expectedCollections));
            Assert.That(SszOwnedUlongCollection.CreatedCount, Is.EqualTo(2));
            Assert.That(SszOwnedUlongCollection.DisposedCount, Is.EqualTo(2));
        }
    }

    private static void AssertDecode<T>(byte[] data, bool valid, Func<T, CustomOwnedChildren> getChildren) where T : ISszCodec<T>
    {
        if (!valid)
        {
            Assert.Throws<InvalidDataException>(() => T.Decode(data, out T _));
            return;
        }

        T.Decode(data, out T decoded);
        try
        {
            ReadOnlySpan<OwnedDecodeValue> children = getChildren(decoded).AsSpan();
            Assert.That(children.Length, Is.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(CustomOwnedChildren.DisposedCount, Is.Zero);
                Assert.That(SszOwnedUlongCollection.DisposedCount, Is.Zero);
                for (int index = 0; index < children.Length; index++)
                    Assert.That(children[index].Items.AsSpan().ToArray(), Is.EqualTo(new ulong[] { (ulong)(index + 1) * 17 }));
            }
        }
        finally
        {
            SszLib.ReleaseDecoded(decoded);
        }
    }

    private static byte[] CreatePayload(bool valid)
    {
        byte[] data = new byte[27];
        BinaryPrimitives.WriteInt32LittleEndian(data, 8);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 26);
        for (int index = 0; index < 2; index++)
        {
            int offset = 8 + index * 9;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), (ulong)(index + 1) * 17);
            data[offset + sizeof(ulong)] = 1;
        }
        data[^1] = valid ? (byte)1 : (byte)2;
        return data;
    }
}

public partial class SszGeneratorDiagnosticTest
{
    [TestCase("Storage<Child>", "", "must declare SszCollectionOwnsElements(true or false)")]
    [TestCase("Child[]", "[SszCollectionOwnsElements(true)]", "its collection has no disposer")]
    public void Invalid_custom_collection_ownership_reports_a_diagnostic(string collectionType, string ownership, string message)
    {
        string source = $$"""
            using System;
            using Nethermind.Serialization.Ssz;
            public sealed class Storage<T> : IDisposable
            {
                public Storage(ReadOnlySpan<T> values) { }
                public Span<T> AsSpan() => default;
                public void Dispose() { }
            }
            [SszContainer]
            public partial struct Child
            {
                [SszVector(1)] public Storage<ulong> Values { get; set; }
            }
            [SszContainer]
            public partial struct Parent
            {
                [SszList(2)] {{ownership}} public {{collectionType}} Children { get; set; }
            }
            """;

        Diagnostic diagnostic = GetSsz003Diagnostic(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Invalid_custom_collection_ownership_reports_a_diagnostic));

        Assert.That(diagnostic.GetMessage(), Does.Contain(message));
    }
}

public sealed class CustomOwnedChildren : IDisposable
{
    private static bool _disposeElements;
    private static bool _failConstruction;
    private readonly OwnedDecodeValue[] _children;
    private readonly bool _ownsElements;

    public static int CreatedCount { get; private set; }
    public static int DisposedCount { get; private set; }

    public CustomOwnedChildren(ReadOnlySpan<OwnedDecodeValue> children)
    {
        if (_failConstruction) throw new InvalidDataException("Collection construction rejected the decoded children.");
        _children = children.ToArray();
        _ownsElements = _disposeElements;
        CreatedCount++;
    }

    public Span<OwnedDecodeValue> AsSpan() => _children;

    public void Dispose()
    {
        if (_ownsElements)
        {
            foreach (OwnedDecodeValue child in _children)
                SszLib.ReleaseDecoded(child);
        }
        DisposedCount++;
    }

    public static void Reset(bool ownsElements, bool failConstruction)
    {
        _disposeElements = ownsElements;
        _failConstruction = failConstruction;
        CreatedCount = DisposedCount = 0;
    }
}

[SszContainer]
public partial struct DeepOwnedDecodeParent
{
    [SszList(2), SszCollectionOwnsElements(true)]
    public CustomOwnedChildren Children { get; set; }

    [SszList(2)]
    public bool[] Flags { get; set; }
}

[SszContainer]
public partial struct ShallowOwnedDecodeParent
{
    [SszList(2), SszCollectionOwnsElements(false)]
    public CustomOwnedChildren Children { get; set; }

    [SszList(2)]
    public bool[] Flags { get; set; }
}
