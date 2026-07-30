// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class BannedConcurrentDictionaryKeysValuesAnalyzerTests
{
    [TestCase("Keys")]
    [TestCase("Values")]
    public async Task ConcurrentDictionary_Keys_or_Values_reports_diagnostic(string member)
    {
        string source = $$"""
            using System.Collections.Concurrent;
            class Test
            {
                void M()
                {
                    var dict = new ConcurrentDictionary<int, int>();
                    var x = {|#0:dict.{{member}}|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments(member));
    }

    [TestCase("Keys")]
    [TestCase("Values")]
    public async Task NonBlocking_ConcurrentDictionary_Keys_or_Values_reports_diagnostic(string member)
    {
        // Stub a NonBlocking.ConcurrentDictionary<TKey,TValue> with the same shape
        // since the analyzer matches on full metadata name.
        string source = $$"""
            namespace NonBlocking
            {
                public class ConcurrentDictionary<TKey, TValue>
                {
                    public System.Collections.Generic.ICollection<TKey> Keys => null!;
                    public System.Collections.Generic.ICollection<TValue> Values => null!;
                }
            }
            class Test
            {
                void M()
                {
                    var dict = new NonBlocking.ConcurrentDictionary<int, int>();
                    var x = {|#0:dict.{{member}}|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments(member));
    }

    [Test]
    public async Task Non_target_patterns_no_diagnostic()
    {
        // Bundle every "not flagged" case: same-named type in a different namespace,
        // plain Dictionary, user-defined Keys/Values properties, and other
        // ConcurrentDictionary members (foreach, TryGetValue, Count, etc.).
        string source = """
            using System.Collections.Generic;
            using System.Collections.Concurrent;
            namespace Other
            {
                public class ConcurrentDictionary<TKey, TValue>
                {
                    public ICollection<TKey> Keys => null!;
                    public ICollection<TValue> Values => null!;
                }
            }
            class MyBag
            {
                public int[] Keys => System.Array.Empty<int>();
                public int[] Values => System.Array.Empty<int>();
            }
            class Test
            {
                void M()
                {
                    var plain = new Dictionary<int, int>();
                    var k1 = plain.Keys; var v1 = plain.Values;

                    var other = new Other.ConcurrentDictionary<int, int>();
                    var k2 = other.Keys; var v2 = other.Values;

                    var bag = new MyBag();
                    var k3 = bag.Keys; var v3 = bag.Values;

                    var cd = new ConcurrentDictionary<int, int>();
                    cd.TryGetValue(1, out _);
                    cd.ContainsKey(1);
                    foreach (var kv in cd) { _ = kv; }
                    var c = cd.Count; var e = cd.IsEmpty;
                }
            }
            """;

        await Verify(source);
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<BannedConcurrentDictionaryKeysValuesAnalyzer, DefaultVerifier>
            .Diagnostic(BannedConcurrentDictionaryKeysValuesAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<BannedConcurrentDictionaryKeysValuesAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
