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

    [Test]
    public async Task NonBlocking_ConcurrentDictionary_no_diagnostic()
    {
        // Stub a NonBlocking.ConcurrentDictionary<TKey,TValue> with the same shape
        // to verify the analyzer matches on the BCL symbol, not the simple name.
        string source = """
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
                    var k = dict.Keys;
                    var v = dict.Values;
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Plain_Dictionary_no_diagnostic()
    {
        string source = """
            using System.Collections.Generic;
            class Test
            {
                void M()
                {
                    var dict = new Dictionary<int, int>();
                    var k = dict.Keys;
                    var v = dict.Values;
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Other_ConcurrentDictionary_members_no_diagnostic()
    {
        string source = """
            using System.Collections.Concurrent;
            class Test
            {
                void M()
                {
                    var dict = new ConcurrentDictionary<int, int>();
                    dict.TryGetValue(1, out _);
                    dict.ContainsKey(1);
                    foreach (var kv in dict) { _ = kv; }
                    var c = dict.Count;
                    var e = dict.IsEmpty;
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Userdefined_type_with_Keys_property_no_diagnostic()
    {
        string source = """
            class MyBag
            {
                public int[] Keys => System.Array.Empty<int>();
                public int[] Values => System.Array.Empty<int>();
            }
            class Test
            {
                void M()
                {
                    var bag = new MyBag();
                    var k = bag.Keys;
                    var v = bag.Values;
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
