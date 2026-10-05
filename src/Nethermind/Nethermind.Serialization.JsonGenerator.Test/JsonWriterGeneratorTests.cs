// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Serialization.JsonGenerator.Test;

/// <summary>
/// A writer for a shape the generator cannot reproduce exactly would change the wire format, so such types must be reported
/// and skipped; the shapes it does accept must write what the metadata path writes.
/// </summary>
public class JsonWriterGeneratorTests
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Nethermind.Serialization.Json;
        """;

    private static IEnumerable<TestCaseData> SkippedShapes()
    {
        yield return Case("NJW001", "hand-written converter", """
            [GenerateJsonWriter] public class Target { public int A { get; set; } }
            public class TargetConverter : JsonConverter<Target>
            {
                public override Target Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;
                public override void Write(Utf8JsonWriter writer, Target value, JsonSerializerOptions options) { }
            }
            """);
        yield return Case("NJW001", "type-level converter", """
            [GenerateJsonWriter, JsonConverter(typeof(TargetConverter))] public class Target { public int A { get; set; } }
            public class TargetConverter : JsonConverterFactory
            {
                public override bool CanConvert(Type typeToConvert) => true;
                public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => null;
            }
            """);
        yield return Case("NJW002", "abstract", "[GenerateJsonWriter] public abstract class Target { public int A { get; set; } }");
        yield return Case("NJW002", "generic", "[GenerateJsonWriter] public class Target<T> { public T A { get; set; } }");
        yield return Case("NJW002", "private nested", "public class Outer { [GenerateJsonWriter] private class Target { public int A { get; set; } } }");
        yield return Case("NJW002", "polymorphic", "[GenerateJsonWriter, JsonDerivedType(typeof(Target), \"t\")] public class Target { public int A { get; set; } }");
        yield return Case("NJW002", "number handling on type", "[GenerateJsonWriter, JsonNumberHandling(JsonNumberHandling.WriteAsString)] public class Target { public int A { get; set; } }");
        yield return Case("NJW003", "public field", "[GenerateJsonWriter] public class Target { public int A; }");
        yield return Case("NJW003", "public field in base", "public class Base { public int A; } [GenerateJsonWriter] public class Target : Base { public int B { get; set; } }");
        yield return Case("NJW003", "included non-public property", "[GenerateJsonWriter] public class Target { [JsonInclude] internal int A { get; set; } }");
        yield return Case("NJW003", "extension data", "[GenerateJsonWriter] public class Target { [JsonExtensionData] public Dictionary<string, object> A { get; set; } }");
        yield return Case("NJW003", "number handling on property", "[GenerateJsonWriter] public class Target { [JsonNumberHandling(JsonNumberHandling.WriteAsString)] public int A { get; set; } }");
        yield return Case("NJW003", "WhenWritingNull on a value type", "[GenerateJsonWriter] public class Target { [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int A { get; set; } }");
        yield return Case("NJW003", "converter for the underlying type of a nullable", """
            [GenerateJsonWriter] public class Target { [JsonConverter(typeof(IntConverter))] public int? A { get; set; } }
            public class IntConverter : JsonConverter<int>
            {
                public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => 0;
                public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) { }
            }
            """);
        yield return Case("NJW003", "name conflict", "[GenerateJsonWriter] public class Target { public int A { get; set; } [JsonPropertyName(\"A\")] public int B { get; set; } }");

        static TestCaseData Case(string id, string name, string source) => new TestCaseData(id, source).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(SkippedShapes))]
    public void Unsupported_shapes_are_reported_and_skipped(string id, string source)
    {
        GeneratorDriverRunResult result = Run(Usings + source, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics.Select(static d => d.Id), Does.Contain(id));
            Assert.That(result.GeneratedTrees, Is.Empty, "no writer and no registration may be emitted for a skipped type");
        }
    }

    private static IEnumerable<TestCaseData> SupportedShapes()
    {
        yield return Case("inheritance, override and new", """
            public class Base
            {
                public virtual string Kind => "base";
                public int Shadowed { get; set; } = 1;
                public string Inherited { get; set; } = "i";
            }
            [GenerateJsonWriter]
            public class Target : Base
            {
                public override string Kind => "target";
                public new string Shadowed { get; set; } = "s";
                public int Own { get; set; } = 2;
            }
            """);
        yield return Case("ignore conditions", """
            [GenerateJsonWriter]
            public class Target
            {
                [JsonIgnore] public int Always { get; set; } = 1;
                [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string NeverNull { get; set; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ZeroDefault { get; set; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int SetDefault { get; set; } = 3;
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NullNullable { get; set; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] public int Hidden { get; set; } = 4;
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)] public int ReadHidden { get; set; } = 5;
                public string DefaultNull { get; set; }
                public int DefaultZero { get; set; }
            }
            """);
        yield return Case("names, order and accessors", """
            [GenerateJsonWriter]
            public class Target
            {
                [JsonPropertyOrder(2)] public int Second { get; set; } = 2;
                [JsonPropertyName("custom_name")] public int Renamed { get; set; } = 1;
                [JsonPropertyOrder(-1)] public int First { get; set; } = 0;
                public int WriteOnly { set { } }
                public int PrivateGetter { private get; set; }
                internal int Internal { get; set; } = 9;
                public static int Static { get; set; } = 8;
                public int this[int i] => i;
            }
            """);
        yield return Case("converters, nested objects and object-typed values", """
            [GenerateJsonWriter]
            public class Target : IJsonOnSerializing, IJsonOnSerialized
            {
                [JsonConverter(typeof(HexConverter))] public int Hex { get; set; } = 255;
                [JsonConverter(typeof(JsonStringEnumConverter))] public DayOfWeek Day { get; set; } = DayOfWeek.Friday;
                public Nested Child { get; set; } = new();
                public object Boxed { get; set; } = new Nested();
                public List<Nested> Items { get; set; } = [new(), new()];
                public int[] Empty { get; set; } = [];
                public long? Optional { get; set; } = 7;
                public int Before { get; private set; }
                [JsonIgnore] public int After { get; private set; }
                void IJsonOnSerializing.OnSerializing() => Before++;
                void IJsonOnSerialized.OnSerialized() => After++;
            }
            public class Nested { public string Name { get; set; } = "n"; }
            public class HexConverter : JsonConverter<int>
            {
                public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => 0;
                public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("x"));
            }
            """);

        static TestCaseData Case(string name, string source) => new TestCaseData(source).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(SupportedShapes))]
    public void Supported_shapes_write_what_the_metadata_path_writes(string source)
    {
        GeneratorDriverRunResult result = Run(Usings + source, out Compilation compilation);
        Assert.That(result.Diagnostics, Is.Empty);

        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(stream);
        Assert.That(emitted.Success, Is.True, string.Join(Environment.NewLine, emitted.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));

        Assembly assembly = Assembly.Load(stream.ToArray());
        Type target = assembly.GetType("Target", throwOnError: true)!;
        JsonConverter writer = (JsonConverter)Activator.CreateInstance(assembly.GetType("TargetJsonWriter", throwOnError: true)!)!;

        // Creating the writer ran the module initializer, which registered generated writers globally; drop them here.
        JsonSerializerOptions metadataOptions = GeneratedJsonWriters.GetMetadataOptions(EthereumJsonSerializer.JsonOptions);
        JsonSerializerOptions options = new(metadataOptions);
        options.Converters.Insert(0, writer);

        // Fresh instances, since serialization callbacks change state the output reflects.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((IGeneratedJsonWriter)writer).IsActive(options), Is.True, "the writer deferred to the metadata path");
            Assert.That(JsonSerializer.Serialize(Activator.CreateInstance(target), target, options), Is.EqualTo(JsonSerializer.Serialize(Activator.CreateInstance(target), target, metadataOptions)));
        }
    }

    [Test]
    public void Registration_lists_every_generated_writer()
    {
        GeneratorDriverRunResult result = Run(Usings + """
            namespace A { [GenerateJsonWriter] public class One { public int X { get; set; } } }
            namespace B { [GenerateJsonWriter] public class Two { public int Y { get; set; } } }
            """, out _);

        string registration = result.GeneratedTrees.Single(static t => Path.GetFileName(t.FilePath) == "GeneratedJsonWriterRegistration.g.cs").GetText().ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(registration, Does.Contain("new global::A.OneJsonWriter()"));
            Assert.That(registration, Does.Contain("new global::B.TwoJsonWriter()"));
        }
    }

    private static GeneratorDriverRunResult Run(string source, out Compilation output)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "JsonWriterGeneratorProbe",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateGenerator()).WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out ImmutableArray<Diagnostic> _);
        return driver.GetRunResult();
    }

    private static IIncrementalGenerator CreateGenerator()
    {
        DirectoryInfo configurationDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo artifactsBinDirectory = configurationDirectory.Parent?.Parent
            ?? throw new InvalidOperationException($"Cannot resolve artifacts/bin from {AppContext.BaseDirectory}");
        string generatorPath = Path.Combine(artifactsBinDirectory.FullName, "Nethermind.Serialization.JsonGenerator", configurationDirectory.Name, "Nethermind.Serialization.JsonGenerator.dll");

        Assembly assembly = Assembly.LoadFrom(generatorPath);
        Type generatorType = assembly.GetType("Nethermind.Serialization.JsonGenerator.JsonWriterGenerator", throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
    }

    private static MetadataReference[] BuildMetadataReferences()
    {
        string trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return
        [
            .. trustedPlatformAssemblies.Split(Path.PathSeparator).Select(static p => MetadataReference.CreateFromFile(p)),
            MetadataReference.CreateFromFile(typeof(GeneratedJsonWriters).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(TypeInfoJsonSerializer).Assembly.Location),
        ];
    }
}
