// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class KeyValueStoreSetToArrayAnalyzerTests
{
    // Minimal stand-ins for the real types so the analyzer resolves them by metadata name.
    private const string StoreStub = """
        namespace Nethermind.Core
        {
            using System;
            public enum WriteFlags { None = 0 }
            public interface IWriteOnlyKeyValueStore
            {
                void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None);
                bool PreferWriteByArray => false;
                void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
                    => Set(key, value.ToArray(), flags);
            }
            public class MemDb : IWriteOnlyKeyValueStore
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
            }
            public sealed class Hash256
            {
                private readonly byte[] _bytes = new byte[32];
                public ReadOnlySpan<byte> Bytes => _bytes;
                public byte[] BytesToArray() => (byte[])_bytes.Clone();
            }
        }
        """;

    [Test]
    public async Task Set_with_span_toarray_reports([Values("Span", "ReadOnlySpan")] string spanKind)
    {
        string source = $$"""
            using System;
            using Nethermind.Core;
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, {{spanKind}}<byte> key, {{spanKind}}<byte> value)
                    => db.Set(key, {|#0:value.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments($"{spanKind}<byte>.ToArray()", "the span"));
    }

    [Test]
    public async Task Set_with_bytes_to_array_reports()
    {
        string source = """
            using System;
            using Nethermind.Core;
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, Hash256 hash)
                    => db.Set(key, {|#0:hash.BytesToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("BytesToArray()", "its .Bytes span"));
    }

    [Test]
    public async Task Set_with_bytes_to_array_on_type_without_bytes_span_no_diagnostic()
    {
        // BytesToArray() exists but the type has no `Bytes` span property, so `.Bytes` is not a valid replacement.
        string source = """
            using System;
            using Nethermind.Core;
            class NoBytesSpan { public byte[] BytesToArray() => new byte[1]; }
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, NoBytesSpan x)
                    => db.Set(key, x.BytesToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Set_through_interface_with_flags_reports()
    {
        string source = """
            using System;
            using Nethermind.Core;
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, Span<byte> value)
                    => db.Set(key, {|#0:value.ToArray()|}, WriteFlags.None);
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("Span<byte>.ToArray()", "the span"));
    }

    [Test]
    public async Task Set_on_concrete_store_no_diagnostic()
    {
        // Bound to MemDb.Set (a concrete override), not the interface member — low-level plumbing where
        // a PreferWriteByArray store deliberately takes an owned array. Not flagged.
        string source = """
            using System;
            using Nethermind.Core;
            class C
            {
                static void Use(MemDb db, ReadOnlySpan<byte> key, Span<byte> value)
                    => db.Set(key, value.ToArray(), WriteFlags.None);
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Set_with_plain_array_value_no_diagnostic()
    {
        // Value is already a byte[]; no span.ToArray() to eliminate.
        string source = """
            using System;
            using Nethermind.Core;
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, byte[] value)
                    => db.Set(key, value);
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Set_with_list_toarray_value_no_diagnostic()
    {
        // ToArray on List<byte> cannot be replaced by a span; PutSpan would not help.
        string source = """
            using System;
            using System.Collections.Generic;
            using Nethermind.Core;
            class C
            {
                static void Use(IWriteOnlyKeyValueStore db, ReadOnlySpan<byte> key, List<byte> value)
                    => db.Set(key, value.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task PutSpan_default_impl_delegating_to_this_Set_no_diagnostic()
    {
        // Rewriting this.Set -> this.PutSpan inside PutSpan would self-recurse.
        string source = """
            using System;
            using Nethermind.Core;
            class MyStore : IWriteOnlyKeyValueStore
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
                public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
                    => Set(key, value.ToArray(), flags);
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task PutSpan_delegating_to_another_interface_typed_store_reports()
    {
        // The Set target is a different, interface-typed store — no self-recursion, so flag it.
        string source = """
            using System;
            using Nethermind.Core;
            class Wrapper : IWriteOnlyKeyValueStore
            {
                private readonly IWriteOnlyKeyValueStore _inner;
                public Wrapper(IWriteOnlyKeyValueStore inner) => _inner = inner;
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) { }
                public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
                    => _inner.Set(key, {|#0:value.ToArray()|}, flags);
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("ReadOnlySpan<byte>.ToArray()", "the span"));
    }

    [Test]
    public async Task Unrelated_Set_method_no_diagnostic()
    {
        // A Set method not implementing IWriteOnlyKeyValueStore must not be flagged.
        string source = """
            using System;
            using Nethermind.Core;
            class NotAStore
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value) { }
                static void Use(NotAStore x, ReadOnlySpan<byte> key, Span<byte> value)
                    => x.Set(key, value.ToArray());
            }
            """;
        await Verify(source);
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<KeyValueStoreSetToArrayAnalyzer, DefaultVerifier>.Diagnostic(KeyValueStoreSetToArrayAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<KeyValueStoreSetToArrayAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source + "\n" + StoreStub,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
