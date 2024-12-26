// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Nethermind.Serialization.FluentRlp.Generator;

public enum RlpRepresentation : byte
{
    /// <summary>
    /// The RLP encoding will be a sequence of RLP objects for each property.
    /// </summary>
    Record = 0,

    /// <summary>
    /// The RLP encoding will be equivalent to the only underlying property.
    /// </summary>
    Newtype = 1,
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class RlpSerializable(RlpRepresentation representation = RlpRepresentation.Record) : Attribute
{
    public RlpRepresentation Representation { get; } = representation;
}

/// <summary>
/// A source generator that finds all records with [RlpSerializable] and
/// generates an abstract RlpConverter class with a Read method.
/// </summary>
[Generator]
public sealed class RlpSourceGenerator : IIncrementalGenerator
{
    private const string Version = "0.1";
    private const string GeneratedCodeAttribute = $"""[GeneratedCode("{nameof(RlpSourceGenerator)}", "{Version}")]""";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (s, _) => s is RecordDeclarationSyntax,
            transform: static (ctx, _) => (RecordDeclarationSyntax)ctx.Node
        );

        var compilation = context.CompilationProvider.Combine(provider.Collect());

        context.RegisterSourceOutput(compilation, Execute);
    }

    private void Execute(
        SourceProductionContext context,
        (Compilation Compilation, ImmutableArray<RecordDeclarationSyntax> RecordsDeclarationSyntaxes) p)
    {
        // For each record with the attribute, generate the RlpConverter class
        foreach (var recordDecl in p.RecordsDeclarationSyntaxes)
        {
            // Check if the record has the [RlpSerializable] attribute
            SemanticModel semanticModel = p.Compilation.GetSemanticModel(recordDecl.SyntaxTree);
            ISymbol? symbol = semanticModel.GetDeclaredSymbol(recordDecl);
            if (symbol is null) continue;

            AttributeData? rlpSerializableAttribute = symbol
                .GetAttributes()
                .FirstOrDefault(a =>
                    a.AttributeClass?.Name == nameof(RlpSerializable) ||
                    a.AttributeClass?.ToDisplayString() == nameof(RlpSerializable));

            // If not, skip the record
            if (rlpSerializableAttribute is null) continue;

            // Extract the fully qualified record name with its namespace
            var recordName = symbol.Name;
            var fullTypeName = symbol.ToDisplayString();
            // TODO: Deal with missing and nested namespaces
            var @namespace = symbol.ContainingNamespace?.ToDisplayString();

            var representation = (RlpRepresentation)(rlpSerializableAttribute.ConstructorArguments[0].Value ?? 0);

            // Gather recursively all members that are fields or primary constructor parameters
            // so we can read them in the same order they are declared.
            var parameters = GetRecordParameters(recordDecl);

            // Ensure `Newtype` is only used in single-property records
            if (representation == RlpRepresentation.Newtype && parameters.Count != 1)
            {
                var descriptor = new DiagnosticDescriptor(
                    "RLP0001",
                    $"Invalid {nameof(RlpRepresentation)}",
                    $"'{nameof(RlpRepresentation.Newtype)}' representation is only allowed for records with a single property",
                    "", DiagnosticSeverity.Error, true);
                context.ReportDiagnostic(Diagnostic.Create(descriptor: descriptor, recordDecl.GetLocation()));

                return;
            }

            // Build the converter class source
            var generatedCode = GenerateConverterClass(@namespace, fullTypeName, recordName, parameters, representation);

            // Add to the compilation
            context.AddSource($"{recordName}RlpConverter.g.cs", SourceText.From(generatedCode, Encoding.UTF8));
        }
    }

    /// <summary>
    /// Gathers the record’s primary constructor parameters and public fields/properties
    /// in the order they appear in the record declaration.
    /// </summary>
    private static List<(string Name, TypeSyntax TypeName)> GetRecordParameters(RecordDeclarationSyntax recordDecl)
    {
        List<(string, TypeSyntax)> parameters = [];

        // Primary constructor parameters
        if (recordDecl.ParameterList is not null)
        {
            foreach (var param in recordDecl.ParameterList.Parameters)
            {
                var paramName = param.Identifier.Text;
                var paramType = param.Type!;

                parameters.Add((paramName, paramType));
            }
        }

        return parameters;
    }

    private static string GenerateConverterClass(
        string? @namespace,
        string fullTypeName,
        string recordName,
        List<(string Name, TypeSyntax TypeName)> parameters,
        RlpRepresentation representation)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.CodeDom.Compiler;");
        sb.AppendLine("using Nethermind.Serialization.FluentRlp;");
        sb.AppendLine("using Nethermind.Serialization.FluentRlp.Instances;");
        sb.AppendLine();
        if (@namespace is not null) sb.AppendLine($"namespace {@namespace};");
        sb.AppendLine("");
        sb.AppendLine(GeneratedCodeAttribute);
        sb.AppendLine($"public abstract class {recordName}RlpConverter : IRlpConverter<{fullTypeName}>");
        sb.AppendLine("{");

        // `Write` method
        sb.AppendLine($"public static void Write(ref RlpWriter w, {fullTypeName} value)");
        sb.AppendLine("{");
        if (representation == RlpRepresentation.Record)
        {
            sb.AppendLine($"w.WriteSequence(value, static (ref RlpWriter w, {fullTypeName} value) => ");
            sb.AppendLine("{");
        }

        foreach (var (name, typeName) in parameters)
        {
            var writeCall = MapTypeToWriteCall(name, typeName);
            sb.AppendLine($"w.{writeCall};");
        }

        if (representation == RlpRepresentation.Record)
        {
            sb.AppendLine("});");
        }

        sb.AppendLine("}");

        // `Read` method
        sb.AppendLine($"public static {fullTypeName} Read(ref RlpReader r)");
        sb.AppendLine("{");
        if (representation == RlpRepresentation.Record)
        {
            sb.AppendLine("return r.ReadSequence(static (scoped ref RlpReader r) =>");
            sb.AppendLine("{");
        }

        foreach (var (name, typeName) in parameters)
        {
            var readCall = MapTypeToReadCall(typeName);
            sb.AppendLine($"var {name} = r.{readCall};");
        }

        sb.Append($"return new {fullTypeName}(");
        for (int i = 0; i < parameters.Count; i++)
        {
            sb.Append(parameters[i].Name);
            if (i < parameters.Count - 1) sb.Append(", ");
        }

        sb.AppendLine(");");

        if (representation == RlpRepresentation.Record)
        {
            sb.AppendLine("});");
        }

        sb.AppendLine("}");
        sb.AppendLine("}");

        sb.AppendLine();
        sb.AppendLine(GeneratedCodeAttribute);
        sb.AppendLine($"public static class {recordName}Ext");
        sb.AppendLine("{");
        sb.AppendLine(
            $"public static {fullTypeName} Read{recordName}(this ref RlpReader reader) => {recordName}RlpConverter.Read(ref reader);");
        sb.AppendLine(
            $"public static void Write(this ref RlpWriter writer, {fullTypeName} value) => {recordName}RlpConverter.Write(ref writer, value);");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Map the type name to the appropriate Read method on the `RlpReader`
    /// Extend this mapping for more types as needed.
    /// </summary>
    private static string MapTypeToReadCall(TypeSyntax syntax)
    {
        // Hard-coded cases
        switch (syntax.ToString())
        {
            case "byte[]" or "Byte[]" or "System.Byte[]":
                return "ReadBytes().ToArray()";
            case "Span<byte>" or "System.Span<byte>" or "ReadOnlySpan<byte>" or "System.ReadOnlySpan<byte>":
                return "ReadBytes()";
        }

        // Arrays
        if (syntax is ArrayTypeSyntax array)
        {
            var elementType = array.ElementType;
            return $"ReadArray(static (scoped ref RlpReader r) => r.{MapTypeToReadCall(elementType)})";
        }

        // Generics
        if (syntax is GenericNameSyntax or TupleTypeSyntax)
        {
            var typeConstructor = syntax switch
            {
                GenericNameSyntax generic => generic.Identifier.ToString(),
                TupleTypeSyntax _ => "Tuple",
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var typeParameters = syntax switch
            {
                GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
                TupleTypeSyntax tuple => tuple.Elements.Select(e => e.Type),
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var sb = new StringBuilder("Read");
            sb.Append(typeConstructor.Capitalize());
            sb.AppendLine("(");

            foreach (var typeParameter in typeParameters)
            {
                sb.Append("static (scoped ref RlpReader r) =>");
                sb.Append("{");
                sb.Append($"return r.{MapTypeToReadCall(typeParameter)};");
                sb.Append("},");
            }

            sb.Length -= 1; // Remove the trailing `,`
            sb.Append(")");

            return sb.ToString();
        }

        // Default
        return $"Read{MapTypeAlias(syntax.ToString())}()";
    }

    /// <summary>
    /// Map the type name to the appropriate Write method on the `RlpWriter`
    /// Extend this mapping for more types as needed.
    /// </summary>
    private static string MapTypeToWriteCall(string name, TypeSyntax syntax)
    {
        // Arrays
        if (syntax is ArrayTypeSyntax array)
        {
            var elementType = array.ElementType;

            switch (elementType.ToString())
            {
                case "byte" or "Byte" or "System.Byte":
                    return $"Write(value.{name})";
                default:
                    return
                        $"Write(value.{name}, static (ref RlpWriter w, {elementType.ToString()} value) => w.Write(value))";
            }
        }

        // Generics and Tuples
        if (syntax is GenericNameSyntax or TupleTypeSyntax)
        {
            var typeParameters = syntax switch
            {
                GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
                TupleTypeSyntax tuple => tuple.Elements.Select(e => e.Type),
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var sb = new StringBuilder("Write");
            sb.AppendLine("(");

            sb.AppendLine($"value.{name},");
            foreach (var typeParameter in typeParameters)
            {
                sb.Append($"static (ref RlpWriter w, {typeParameter.ToString()} value) =>");
                sb.Append("{");
                sb.Append("w.Write(value);");
                sb.Append("},");
            }

            sb.Length -= 1; // Remove the trailing `,`
            sb.Append(")");

            return sb.ToString();
        }

        // Default
        return $"Write(value.{name})";
    }

    private static string MapTypeAlias(string alias) =>
        alias switch
        {
            "string" => "String",
            "short" => "Int16",
            "int" => "Int32",
            "long" => "Int64",
            _ => alias
        };
}

public static class StringExt
{
    public static string Capitalize(this string str) => str[0].ToString().ToUpper() + str[1..];
}

public static class EnumerableExt
{
    public static IEnumerable<T> Intersperse<T>(this IEnumerable<T> source, T element)
    {
        bool first = true;
        foreach (T value in source)
        {
            if (!first) yield return element;
            yield return value;
            first = false;
        }
    }
}
