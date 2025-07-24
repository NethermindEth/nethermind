// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
public sealed class RlpSerializable(RlpRepresentation representation = RlpRepresentation.Record, int length = -1) : Attribute
{
    public RlpRepresentation Representation { get; } = representation;
    public int Length { get; } = length;
}

/// <summary>
/// A source generator that finds all records with [RlpSerializable] attribute and
/// generates an abstract `IRlpConverter` class with `Read` and `Write` methods.
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

            // Extract all `using` directives
            var usingDirectives = semanticModel.SyntaxTree.GetCompilationUnitRoot()
                .Usings
                .Select(u => u.ToString())
                .ToList();

            // Get the `RlpRepresentation` mode
            var representation = (RlpRepresentation)(rlpSerializableAttribute.ConstructorArguments[0].Value ?? 0);

            // Get the constant length if specified
            var constLength = (int)(rlpSerializableAttribute.ConstructorArguments[1].Value ?? -1);

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
            var generatedCode = GenerateConverterClass(@namespace, usingDirectives, fullTypeName, recordName, parameters, representation, constLength);

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
        List<string> usingDirectives,
        string fullTypeName,
        string recordName,
        List<(string Name, TypeSyntax TypeName)> parameters,
        RlpRepresentation representation,
        int constLength)
    {
        List<string> defaultUsingDirectives =
        [
            "using System;",
            "using System.CodeDom.Compiler;",
            "using Nethermind.Serialization.FluentRlp;",
            "using Nethermind.Serialization.FluentRlp.Instances;"
        ];
        IEnumerable<string> directives = defaultUsingDirectives.Concat(usingDirectives).Distinct();
        var usingStatements = new StringBuilder();
        foreach (var usingDirective in directives)
        {
            usingStatements.AppendLine(usingDirective);
        }

        var writeCalls = new StringBuilder();
        foreach (var (name, typeName) in parameters)
        {
            var writeCall = MapTypeToWriteCall(name, typeName);
            writeCalls.AppendLine($"w.{writeCall};");
        }

        var readCalls = new StringBuilder();
        foreach (var (name, typeName) in parameters)
        {
            var readCall = MapTypeToReadCall(typeName);
            readCalls.AppendLine($"var {name} = r.{readCall};");
        }

        var constructorCall = new StringBuilder($"{fullTypeName}(");
        for (int i = 0; i < parameters.Count; i++)
        {
            constructorCall.Append(parameters[i].Name);
            if (i < parameters.Count - 1) constructorCall.Append(", ");
        }
        constructorCall.Append(");");

        return
            $$"""
              // <auto-generated />
              #nullable enable
              {{usingStatements}}
              {{(@namespace is null ? "" : $"namespace {@namespace};")}}

              {{GeneratedCodeAttribute}}
              public abstract class {{recordName}}RlpConverter : IRlpConverter<{{fullTypeName}}>
              {
                  public static void Write(ref RlpWriter w, {{fullTypeName}} value)
                  {
                    {{(constLength > 0
                        ? $"if (w.UNSAFE_FixedLength({constLength})) return;"
                        : "")}}

                    {{(representation == RlpRepresentation.Record
                        ? $$"""
                            w.WriteSequence(value, static (ref RlpWriter w, {{fullTypeName}} value) =>
                            {
                                {{writeCalls}}
                            });
                            """
                        : writeCalls)}}
                  }

                  public static {{fullTypeName}} Read(ref RlpReader r)
                  {
                      {{(representation == RlpRepresentation.Record
                              ? $$"""
                                  return r.ReadSequence(static (scoped ref RlpReader r) =>
                                  {
                                      {{readCalls}}

                                      return new {{constructorCall}}
                                  });
                                  """
                              : $"""
                                 {readCalls}

                                 return new {constructorCall}
                                 """)}}
                  }
              }

              {{GeneratedCodeAttribute}}
              public static class {{recordName}}Ext
              {
                  public static {{fullTypeName}} Read{{recordName}}(this ref RlpReader reader) => {{recordName}}RlpConverter.Read(ref reader);
                  public static void Write(this ref RlpWriter writer, {{fullTypeName}} value) => {{recordName}}RlpConverter.Write(ref writer, value);
              }
              """;
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

        // Generics
        if (syntax is GenericNameSyntax or TupleTypeSyntax or ArrayTypeSyntax)
        {
            var typeConstructor = syntax switch
            {
                GenericNameSyntax generic => generic.Identifier.ToString(),
                TupleTypeSyntax _ => "Tuple",
                ArrayTypeSyntax _ => "Array",
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var typeParameters = syntax switch
            {
                GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
                TupleTypeSyntax tuple => tuple.Elements.Select(e => e.Type),
                ArrayTypeSyntax array => [array.ElementType],
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var sb = new StringBuilder();
            sb.AppendLine($"Read{typeConstructor.Capitalize()}(");
            foreach (var typeParameter in typeParameters)
            {
                sb.AppendLine($$"""static (scoped ref RlpReader r) => { return r.{{MapTypeToReadCall(typeParameter)}}; },""");
            }
            sb.Length -= 2; // Remove the trailing `,\n`
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
    private static string MapTypeToWriteCall(string? propertyName, TypeSyntax syntax)
    {
        // Hard-coded cases
        switch (syntax.ToString())
        {
            case "byte[]" or "Byte[]" or "System.Byte[]" or "Span<byte>" or "System.Span<byte>" or "ReadOnlySpan<byte>" or "System.ReadOnlySpan<byte>":
                return propertyName is null ? "Write(value)" : $"Write(value.{propertyName})";
        }

        // Generics
        if (syntax is GenericNameSyntax or TupleTypeSyntax or ArrayTypeSyntax)
        {
            var typeParameters = syntax switch
            {
                GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
                TupleTypeSyntax tuple => tuple.Elements.Select(e => e.Type),
                ArrayTypeSyntax array => [array.ElementType],
                _ => throw new ArgumentOutOfRangeException(nameof(syntax))
            };

            var sb = new StringBuilder();
            sb.AppendLine(propertyName is null ? "Write(value," : $"Write(value.{propertyName},");
            foreach (var typeParameter in typeParameters)
            {
                sb.AppendLine($$"""static (ref RlpWriter w, {{typeParameter}} value) => { w.{{MapTypeToWriteCall(null, typeParameter)}}; },""");
            }
            sb.Length -= 2; // Remove the trailing `,\n`
            sb.Append(")");

            return sb.ToString();
        }

        // Default
        return propertyName is not null ? $"Write(value.{propertyName})" : "Write(value)";
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
    public static string Capitalize(this string str) => str[0].ToString().ToUpper() + str.Substring(1);
}
