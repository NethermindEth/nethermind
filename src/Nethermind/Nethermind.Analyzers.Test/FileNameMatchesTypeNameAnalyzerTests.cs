// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class FileNameMatchesTypeNameAnalyzerTests
{
    [TestCase("00_Foo.cs", "class Foo { }", TestName = "Numeric_prefix_match")]
    [TestCase("Foo.cs", "class Foo { }", TestName = "Class_match")]
    [TestCase("MyAttribute.cs", "class MyAttribute { }", TestName = "Attribute_full_name")]
    [TestCase("My.cs", "class MyAttribute { }", TestName = "Attribute_stripped_name")]
    [TestCase("Foo.cs", "partial class Foo { }", TestName = "Partial_root_file")]
    [TestCase("Foo.Bar.cs", "partial class Foo { }", TestName = "Partial_with_descriptor")]
    [TestCase("Foo.Part.One.cs", "partial class Foo { }", TestName = "Partial_with_multi_segment_descriptor")]
    [TestCase("Foo.std.cs", "partial class Foo { }", TestName = "Partial_with_lowercase_descriptor")]
    [TestCase("Foo.Bar.cs", "partial class FooAttribute { }", TestName = "Partial_attribute_stripped_with_descriptor")]
    [TestCase("Bar.cs", "class Foo { } class Baz { }", TestName = "Multiple_types_skipped")]
    [TestCase("Foo.cs", "class Foo { class Nested { } }", TestName = "Nested_type_ignored")]
    [TestCase("Foo.cs", "class Foo<T> { }", TestName = "Generic_type_matching_name")]
    [TestCase("Foo.T.cs", "class Foo<T> { }", TestName = "Generic_type_descriptor_form")]
    [TestCase("Foo.T.cs", "partial class Foo<T> { }", TestName = "Generic_type_descriptor_form_partial")]
    [TestCase("Foo.cs", "struct Foo { }", TestName = "Struct")]
    [TestCase("Foo.cs", "record Foo;", TestName = "Record")]
    [TestCase("IFoo.cs", "interface IFoo { }", TestName = "Interface")]
    [TestCase("FooEnum.cs", "enum FooEnum { A }", TestName = "Enum")]
    [TestCase("FooDel.cs", "delegate void FooDel();", TestName = "Delegate")]
    [TestCase("Foo.cs", "namespace N { class Foo { } }", TestName = "Block_scoped_namespace_match")]
    [TestCase("Foo.cs", "namespace N;\nclass Foo { }", TestName = "File_scoped_namespace_match")]
    [TestCase("Bar.cs", "[assembly: System.CLSCompliant(true)]", TestName = "No_top_level_types_skipped")]
    public async Task No_diagnostic(string fileName, string source) =>
        await Verify(fileName, source);

    [TestCase("Bar.cs", "class {|#0:Foo|} { }", "Bar", "Foo", TestName = "Class_mismatch")]
    [TestCase("Bar.cs", "class {|#0:Foo|}<T> { }", "Bar", "Foo", TestName = "Generic_type_mismatched_name")]
    [TestCase("FooT.cs", "class {|#0:Foo|}<T> { }", "FooT", "Foo", TestName = "Generic_type_T_suffix_mismatch")]
    [TestCase("My.cs", "class {|#0:MyThing|} { }", "My", "MyThing", TestName = "Non_attribute_suffix_mismatch")]
    [TestCase("Foo.Bar.cs", "class {|#0:Foo|} { }", "Foo.Bar", "Foo", TestName = "Non_partial_with_descriptor_suffix")]
    [TestCase("Bar.cs", "namespace N { class {|#0:Foo|} { } }", "Bar", "Foo", TestName = "Block_scoped_namespace_mismatch")]
    [TestCase("Bar.cs", "namespace N;\nclass {|#0:Foo|} { }", "Bar", "Foo", TestName = "File_scoped_namespace_mismatch")]
    public async Task Reports_diagnostic(string fileName, string source, string fileBaseName, string typeName) =>
        await Verify(fileName, source, Diagnostic().WithLocation(0).WithArguments(fileBaseName, typeName));

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<FileNameMatchesTypeNameAnalyzer, DefaultVerifier>
            .Diagnostic(FileNameMatchesTypeNameAnalyzer.DiagnosticId);

    private static async Task Verify(string fileName, string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<FileNameMatchesTypeNameAnalyzer, DefaultVerifier> test = new()
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.Sources.Add((fileName, source));
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
