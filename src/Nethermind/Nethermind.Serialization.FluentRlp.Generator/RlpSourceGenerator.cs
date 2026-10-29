// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Serialization.FluentRlp.Generator;

/// <summary>
/// Generates an <c>IRlpConverter</c> class and extension methods for each record with <c>[RlpSerializable]</c>.
/// </summary>
[Generator]
public sealed class RlpSourceGenerator : IIncrementalGenerator
{
    private const string Version = "0.2";
    private const string GeneratedCodeAttribute = $"""[global::System.CodeDom.Compiler.GeneratedCode("{nameof(RlpSourceGenerator)}", "{Version}")]""";
    private const string AttributeMetadataName = "Nethermind.Serialization.FluentRlp.RlpSerializableAttribute";
    private const string FixedLengthMetadataName = "Nethermind.Serialization.FluentRlp.RlpFixedLengthAttribute";
    private const string Library = "global::Nethermind.Serialization.FluentRlp";
    private const string Instances = $"{Library}.Instances";

    // Values of Nethermind.Serialization.FluentRlp.RlpRepresentation
    private const int NewtypeRepresentation = 1;

    private static readonly DiagnosticDescriptor InvalidNewtype = new(
        "RLP0001", "Invalid RlpRepresentation",
        "'Newtype' representation is only allowed for records with a single primary constructor parameter",
        "RLP", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor MemberNotSerialized = new(
        "RLP0002", "Member is not serialized",
        "'{0}' is not a primary constructor parameter, so its value would be lost; move it to the primary constructor",
        "RLP", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor UnsupportedRecord = new(
        "RLP0003", "Unsupported record",
        "Record '{0}' is not supported: {1}",
        "RLP", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "RLP0004", "Unsupported type",
        "Type '{0}' of '{1}' is not supported",
        "RLP", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor InvalidLength = new(
        "RLP0005", "Invalid length",
        "A length is only allowed for a 'Newtype' record over a byte array",
        "RLP", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor RecursiveNewtype = new(
        "RLP0006", "Recursive Newtype",
        "'Newtype' record '{0}' contains itself without a sequence around it, so reading it would never end",
        "RLP", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Output> outputs = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            predicate: static (node, _) => node is RecordDeclarationSyntax,
            transform: static (ctx, ct) => Generate(ctx, ct));

        context.RegisterSourceOutput(outputs, static (spc, output) =>
        {
            foreach (Diagnostic diagnostic in output.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (output.Source is not null) spc.AddSource(output.HintName, SourceText.From(output.Source, Encoding.UTF8));
        });
    }

    private sealed class Output(string hintName, string? source, ImmutableArray<Diagnostic> diagnostics)
    {
        public string HintName { get; } = hintName;
        public string? Source { get; } = source;
        public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;
    }

    private static Output Generate(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        RecordDeclarationSyntax recordDecl = (RecordDeclarationSyntax)ctx.TargetNode;
        INamedTypeSymbol symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        string hintName = $"{symbol.ToDisplayString()}.RlpConverter.g.cs";
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Location location = recordDecl.Identifier.GetLocation();

        AttributeData attribute = ctx.Attributes[0];
        // Roslyn boxes an enum argument as its underlying type, which is byte for RlpRepresentation.
        int representation = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is { } r ? System.Convert.ToInt32(r) : 0;
        int constLength = attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int l ? l : -1;

        if (symbol.IsGenericType) diagnostics.Add(Diagnostic.Create(UnsupportedRecord, location, symbol.Name, "generic records are not supported"));
        if (symbol.ContainingType is not null) diagnostics.Add(Diagnostic.Create(UnsupportedRecord, location, symbol.Name, "nested records are not supported"));
        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            diagnostics.Add(Diagnostic.Create(UnsupportedRecord, location, symbol.Name, "derived records are not supported"));
        }

        IMethodSymbol? primaryConstructor = symbol.InstanceConstructors.FirstOrDefault(static c =>
            c.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is RecordDeclarationSyntax));
        if (primaryConstructor is null || primaryConstructor.Parameters.Length == 0)
        {
            diagnostics.Add(Diagnostic.Create(UnsupportedRecord, location, symbol.Name, "a primary constructor with at least one parameter is required"));
        }

        ImmutableArray<IParameterSymbol> parameterSymbols = primaryConstructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
        foreach (ISymbol member in symbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!member.IsStatic && LosesData(member, parameterSymbols))
            {
                diagnostics.Add(Diagnostic.Create(MemberNotSerialized, member.Locations.FirstOrDefault() ?? location, member.Name));
            }
        }

        List<(string Name, ITypeSymbol Type)> parameters = [];
        foreach (IParameterSymbol parameterSymbol in parameterSymbols)
        {
            // A keyword such as `class` needs its `@` prefix in member access.
            string name = SyntaxFacts.GetKeywordKind(parameterSymbol.Name) == SyntaxKind.None ? parameterSymbol.Name : $"@{parameterSymbol.Name}";
            parameters.Add((name, parameterSymbol.Type));
        }

        bool isNewtype = representation == NewtypeRepresentation;
        if (isNewtype && parameters.Count != 1) diagnostics.Add(Diagnostic.Create(InvalidNewtype, location));
        if (constLength >= 0 && !(isNewtype && parameters.Count == 1 && IsByteArray(parameters[0].Type)))
        {
            diagnostics.Add(Diagnostic.Create(InvalidLength, location));
        }

        if (isNewtype && parameters.Count == 1 && ContainsWithoutSequence(parameters[0].Type, symbol))
        {
            diagnostics.Add(Diagnostic.Create(RecursiveNewtype, location, symbol.Name));
        }

        TypeMapper mapper = new(diagnostics, location);
        List<string> readCalls = [];
        List<string> writeCalls = [];
        List<int?> fixedLengths = [];
        for (int i = 0; i < parameters.Count; i++)
        {
            (string name, ITypeSymbol type) = parameters[i];
            fixedLengths.Add(FixedLength(type, parameterSymbols[i]));
            readCalls.Add(mapper.Read(type, name));
            writeCalls.Add(mapper.Write(type, name, $"value.{name}"));
        }

        if (diagnostics.Count > 0) return new Output(hintName, null, diagnostics.ToImmutable());

        // A record whose field lengths can be computed gets GetLength and writes its prefix at once, without a delegate.
        string? payloadLength = null;
        if (CanMeasure(symbol, null, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
        {
            payloadLength = string.Join(" + ", Enumerable.Range(0, parameters.Count)
                .Select(i => mapper.Length(parameters[i].Type, parameterSymbols[i], $"value.{parameters[i].Name}")));
        }

        string source = GenerateConverterClass(symbol, recordDecl, readCalls, writeCalls, isNewtype, constLength, parameters.Count > 0 ? parameters[0].Name : "", payloadLength, mapper.Helpers, fixedLengths);
        return new Output(hintName, source, ImmutableArray<Diagnostic>.Empty);
    }

    /// <summary>Whether <paramref name="member"/> holds a value that the primary constructor parameters do not carry.</summary>
    private static bool LosesData(ISymbol member, ImmutableArray<IParameterSymbol> parameters)
    {
        SyntaxNode? syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        switch (member, syntax)
        {
            case (IFieldSymbol field, VariableDeclaratorSyntax declarator):
                return !(field.IsReadOnly && declarator.Initializer is not null);
            case (IPropertySymbol property, PropertyDeclarationSyntax declaration):
                bool isParameter = parameters.Any(p => p.Name == property.Name);
                // A redeclared positional property must take the parameter value unchanged.
                if (isParameter) return declaration.Initializer?.Value is not IdentifierNameSyntax { Identifier.ValueText: var name } || name != property.Name;
                return property.SetMethod is not null && IsAutoProperty(declaration);
            default:
                return false;
        }
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax property)
        => property.AccessorList is not null && property.AccessorList.Accessors.All(static a => a.Body is null && a.ExpressionBody is null);

    /// <summary>Whether reading <paramref name="type"/> can read <paramref name="record"/> again before it reads a sequence prefix.</summary>
    private static bool ContainsWithoutSequence(ITypeSymbol type, INamedTypeSymbol record)
        => SymbolEqualityComparer.Default.Equals(type, record)
           || type is INamedTypeSymbol { IsTupleType: true } tuple && tuple.TupleElements.Any(e => ContainsWithoutSequence(e.Type, record));

    /// <summary>The encoded length of a byte string of exactly <c>RlpFixedLength</c> bytes, or <see langword="null"/>.</summary>
    private static int? FixedLength(ITypeSymbol type, IParameterSymbol? parameter)
    {
        int length = FixedLengthOf(parameter) ?? FixedLengthOf(type) ?? NewtypeLengthOf(type) ?? -1;

        // A single byte has two encodings, so only longer byte strings have a known length.
        if (length < 2) return null;
        return length + (length <= 55 ? 1 : 1 + (length < 0x100 ? 1 : length < 0x10000 ? 2 : length < 0x1000000 ? 3 : 4));
    }

    private static int? FixedLengthOf(ISymbol? symbol)
        => symbol?.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == FixedLengthMetadataName) is { } attribute
           && attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int length
            ? length
            : null;

    private static int? NewtypeLengthOf(ITypeSymbol type)
        => type.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.ToDisplayString() == AttributeMetadataName) is { } attribute
           && attribute.ConstructorArguments.Length > 1
           && attribute.ConstructorArguments[0].Value is { } representation && System.Convert.ToInt32(representation) == NewtypeRepresentation
           && attribute.ConstructorArguments[1].Value is int length
            ? length
            : null;

    /// <summary>Whether the encoded length of a value of <paramref name="type"/> can be computed without writing it.</summary>
    private static bool CanMeasure(ITypeSymbol type, IParameterSymbol? parameter, HashSet<ITypeSymbol> visiting)
    {
        if (FixedLength(type, parameter) is not null || IsByteArray(type) || TypeMapper.IsInteger(type) || IsUInt256(type)) return true;

        switch (type)
        {
            case IArrayTypeSymbol { Rank: 1 } array:
                return CanMeasure(array.ElementType, null, visiting);
            case INamedTypeSymbol { IsTupleType: true } tuple when tuple.TupleElements.Length == 2:
                return CanMeasure(tuple.TupleElements[0].Type, null, visiting) && CanMeasure(tuple.TupleElements[1].Type, null, visiting);
            case INamedTypeSymbol named when TypeMapper.IsCollection(named, "List", 1):
                return CanMeasure(named.TypeArguments[0], null, visiting);
            case INamedTypeSymbol { IsGenericType: false } named when IsSerializable(named):
                // A record that contains itself through a collection has a finite length for any value.
                if (!visiting.Add(named)) return true;
                IMethodSymbol? constructor = PrimaryConstructor(named);
                bool result = constructor is not null && constructor.Parameters.All(p => CanMeasure(p.Type, p, visiting));
                visiting.Remove(named);
                return result;
            default:
                return false;
        }
    }

    private static bool IsSerializable(INamedTypeSymbol type)
        => type.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == AttributeMetadataName);

    private static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type)
        => type.InstanceConstructors.FirstOrDefault(static c => c.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is RecordDeclarationSyntax));

    private static bool IsUInt256(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "UInt256", ContainingNamespace.Name: "Int256", ContainingNamespace.ContainingNamespace.Name: "Nethermind" };

    private static string Indent(IEnumerable<string> lines, int spaces)
        => string.Join("\n", lines.Select(line => new string(' ', spaces) + line));

    private static bool IsByteArray(ITypeSymbol type)
        => type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte };

    private static string GenerateConverterClass(
        INamedTypeSymbol symbol,
        RecordDeclarationSyntax recordDecl,
        List<string> readCalls,
        List<string> writeCalls,
        bool isNewtype,
        int constLength,
        string firstParameter,
        string? payloadLength,
        List<string> helpers,
        List<int?> fixedLengths)
    {
        string recordName = symbol.Name;
        string fullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string? @namespace = symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString();

        // Read{Type}() and Write(value) of other types bind to extension methods, so bring the usings of the record into scope.
        IEnumerable<string> usingDirectives = new[] { "using Nethermind.Serialization.FluentRlp.Instances;" }
            .Concat(recordDecl.SyntaxTree.GetCompilationUnitRoot().Usings.Select(static u => u.ToString()))
            .Concat(recordDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(static n => n.Usings).Select(static u => u.ToString()))
            .Distinct();
        string accessibility = symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        List<string> writeLines = [];
        if (constLength >= 0)
        {
            writeLines.Add($"""if (value.{firstParameter}.Length != {constLength}) throw new global::System.ArgumentException("{recordName} must have {constLength} bytes", nameof(value));""");
        }
        writeLines.AddRange(writeCalls.Select(static c => $"{c};"));

        // A value with a declared length must have exactly that encoded length on read too.
        List<string> readLines = [];
        for (int i = 0; i < readCalls.Count; i++)
        {
            if (fixedLengths[i] is not { } fixedLength)
            {
                readLines.Add($"var _{i} = {readCalls[i]};");
                continue;
            }

            readLines.Add($"int _{i}Start = r.BytesRead;");
            readLines.Add($"var _{i} = {readCalls[i]};");
            readLines.Add($"""if (r.BytesRead - _{i}Start != {fixedLength}) throw new {Library}.RlpReaderException("RLP item of {recordName} has another length than declared");""");
        }

        if (constLength >= 0)
        {
            readLines.Add($"""if (_0.Length != {constLength}) throw new {Library}.RlpReaderException("RLP byte string of {recordName} must have {constLength} bytes");""");
        }
        readLines.Add($"return new {fullTypeName}({string.Join(", ", Enumerable.Range(0, readCalls.Count).Select(static i => $"_{i}"))});");

        string writeBody = isNewtype
            ? Indent(writeLines, 8)
            : payloadLength is not null
                ? $$"""
                            {{Library}}.RlpSequence sequence = w.StartSequence(GetPayloadLength(value));
                    {{Indent(writeLines, 8)}}
                            w.EndSequence(sequence);
                    """
                : $$"""
                            w.WriteSequence(value, static (ref {{Library}}.RlpWriter w, {{fullTypeName}} value) =>
                            {
                    {{Indent(writeLines, 12)}}
                            });
                    """;

        string converterInterface = payloadLength is null ? "IRlpConverter" : "IRlpLengthConverter";
        string lengthMembers = payloadLength is null
            ? ""
            : isNewtype
                ? $$"""
                        public static int GetLength({{fullTypeName}} value) => {{payloadLength}};

                    """
                : $$"""
                        public static int GetLength({{fullTypeName}} value) => {{Library}}.Rlp.LengthOfSequence(GetPayloadLength(value));

                        private static int GetPayloadLength({{fullTypeName}} value) => {{payloadLength}};

                    """;

        // Reading through EnterSequence and ExitSequence instead of ReadSequence needs no delegate.
        if (!isNewtype) readLines.Insert(readLines.Count - 1, "outer.ExitSequence(in r, itemLength);");
        string readBody = isNewtype
            ? $"        ref {Library}.RlpReader r = ref outer;\n{Indent(readLines, 8)}"
            : $"        {Library}.RlpReader r = outer.EnterSequence(out int itemLength);\n{Indent(readLines, 8)}";

        return $$"""
                 // <auto-generated />
                 #nullable enable
                 {{string.Join("\n", usingDirectives)}}

                 {{(@namespace is null ? "" : $"namespace {@namespace};")}}

                 {{GeneratedCodeAttribute}}
                 {{accessibility}} abstract class {{recordName}}RlpConverter : {{Library}}.{{converterInterface}}<{{fullTypeName}}>
                 {
                     public static void Write(ref {{Library}}.RlpWriter w, {{fullTypeName}} value)
                     {
                 {{writeBody}}
                     }

                 {{lengthMembers + string.Join("\n", helpers)}}
                     public static {{fullTypeName}} Read(ref {{Library}}.RlpReader outer)
                     {
                 {{readBody}}
                     }
                 }

                 {{GeneratedCodeAttribute}}
                 {{accessibility}} static class {{recordName}}RlpConverterExt
                 {
                     public static {{fullTypeName}} Read{{recordName}}(this ref {{Library}}.RlpReader reader) => {{recordName}}RlpConverter.Read(ref reader);
                     public static void Write(this ref {{Library}}.RlpWriter writer, {{fullTypeName}} value) => {{recordName}}RlpConverter.Write(ref writer, value);
                 }

                 """;
    }

    /// <summary>Maps a type to the expressions that read and write it.</summary>
    private sealed class TypeMapper(ImmutableArray<Diagnostic>.Builder diagnostics, Location location)
    {
        private int _helperCount;

        /// <summary>Gets the static helper methods that the read expressions call.</summary>
        public List<string> Helpers { get; } = [];

        public string Read(ITypeSymbol type, string member)
        {
            string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (IsByteArray(type)) return "r.ReadBytes().ToArray()";
            if (IsInteger(type)) return $"r.ReadInteger<{typeName}>()";
            if (type.SpecialType == SpecialType.System_String) return $"{Instances}.StringRlpConverter.Read(ref r)";

            switch (type)
            {
                case IArrayTypeSymbol { Rank: 1 } array:
                    return ReadItems(array.ElementType, member, ".ToArray()");
                case INamedTypeSymbol { IsTupleType: true } tuple when tuple.TupleElements.Length == 2:
                    return $"{Instances}.TupleRlpConverter<{Name(tuple.TupleElements[0].Type)}, {Name(tuple.TupleElements[1].Type)}>.Read(ref r, {ReadLambda(tuple.TupleElements[0].Type, member)}, {ReadLambda(tuple.TupleElements[1].Type, member)})";
                case INamedTypeSymbol named when IsCollection(named, "List", 1):
                    return ReadItems(named.TypeArguments[0], member, "");
                case INamedTypeSymbol named when IsCollection(named, "Dictionary", 2) && HasValueEquality(named.TypeArguments[0]):
                    return $"{Instances}.DictionaryRlpConverter<{Name(named.TypeArguments[0])}, {Name(named.TypeArguments[1])}>.Read(ref r, {ReadLambda(named.TypeArguments[0], member)}, {ReadLambda(named.TypeArguments[1], member)})";
                case INamedTypeSymbol named when IsConverterConvention(named):
                    return $"r.Read{named.Name}()";
            }

            diagnostics.Add(Diagnostic.Create(UnsupportedType, location, typeName, member));
            return "default!";
        }

        public string Write(ITypeSymbol type, string member, string value)
        {
            if (IsByteArray(type) || IsInteger(type)) return $"w.Write({value})";
            if (type.SpecialType == SpecialType.System_String) return $"{Instances}.StringRlpConverter.Write(ref w, {value})";

            switch (type)
            {
                case IArrayTypeSymbol { Rank: 1 } array:
                    return WriteItems(type, array.ElementType, member, value);
                case INamedTypeSymbol { IsTupleType: true } tuple when tuple.TupleElements.Length == 2:
                    return $"{Instances}.TupleRlpConverter<{Name(tuple.TupleElements[0].Type)}, {Name(tuple.TupleElements[1].Type)}>.Write(ref w, {value}, {WriteLambda(tuple.TupleElements[0].Type, member)}, {WriteLambda(tuple.TupleElements[1].Type, member)})";
                case INamedTypeSymbol named when IsCollection(named, "List", 1):
                    return WriteItems(type, named.TypeArguments[0], member, value);
                case INamedTypeSymbol named when IsCollection(named, "Dictionary", 2) && HasValueEquality(named.TypeArguments[0]):
                    return $"{Instances}.DictionaryRlpConverter<{Name(named.TypeArguments[0])}, {Name(named.TypeArguments[1])}>.Write(ref w, {value}, {WriteLambda(named.TypeArguments[0], member)}, {WriteLambda(named.TypeArguments[1], member)})";
                case INamedTypeSymbol named when IsConverterConvention(named):
                    return $"w.Write({value})";
            }

            // Read already reported the type.
            return "_ = 0";
        }

        // Lists and arrays loop in place, so each item costs no delegate call.
        private string ReadItems(ITypeSymbol itemType, string member, string conversion)
        {
            string name = $"ReadItems{_helperCount++}";
            string itemTypeName = Name(itemType);
            string read = Read(itemType, member);
            int? fixedItemLength = FixedLength(itemType, null);

            // Items of a fixed length need no pass over their prefixes to size the list.
            string capacity = fixedItemLength is { } length ? $"r.Remaining / {length}" : "r.CountItems()";
            string add = fixedItemLength is { } itemLength
                ? $$"""
                            int start = r.BytesRead;
                            items.Add({{read}});
                            if (r.BytesRead - start != {{itemLength}}) throw new {{Library}}.RlpReaderException("RLP item has another length than declared");
                """
                : $"                items.Add({read});";
            Helpers.Add($$"""
                    private static global::System.Collections.Generic.List<{{itemTypeName}}> {{name}}(ref {{Library}}.RlpReader outer)
                    {
                        {{Library}}.RlpReader r = outer.EnterSequence(out int itemLength);
                        global::System.Collections.Generic.List<{{itemTypeName}}> items = new({{capacity}});
                        while (r.HasNext)
                        {
                {{add}}
                        }

                        outer.ExitSequence(in r, itemLength);
                        return items;
                    }

                """);
            return $"{name}(ref r){conversion}";
        }

        private string WriteItems(ITypeSymbol type, ITypeSymbol itemType, string member, string value)
        {
            if (!CanMeasure(itemType, null, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
            {
                return $"w.WriteSequence({value}, static (ref {Library}.RlpWriter w, {Name(type)} items) => {{ foreach ({Name(itemType)} item in items) {{ {Write(itemType, member, "item")}; }} }})";
            }

            string name = $"WriteItems{_helperCount++}";
            string write = Write(itemType, member, "item");
            Helpers.Add($$"""
                    private static void {{name}}(ref {{Library}}.RlpWriter w, {{Name(type)}} items)
                    {
                        {{Library}}.RlpSequence sequence = w.StartSequence({{PayloadOfItems(type, itemType, member, "items")}});
                        foreach ({{Name(itemType)}} item in items)
                        {
                            {{write}};
                        }

                        w.EndSequence(sequence);
                    }

                """);
            return $"{name}(ref w, {value})";
        }

        /// <summary>An expression for the encoded length of <paramref name="value"/>; the type must pass <c>CanMeasure</c>.</summary>
        public string Length(ITypeSymbol type, IParameterSymbol? parameter, string value)
        {
            if (FixedLength(type, parameter) is { } fixedLength) return fixedLength.ToString();
            if (IsByteArray(type)) return $"{Library}.Rlp.LengthOf({value})";
            if (IsInteger(type)) return $"{Library}.Rlp.LengthOf<{Name(type)}>({value})";
            if (IsUInt256(type)) return $"{Instances}.UInt256RlpConverter.GetLength({value})";

            switch (type)
            {
                case IArrayTypeSymbol { Rank: 1 } array:
                    return $"{Library}.Rlp.LengthOfSequence({PayloadOfItems(type, array.ElementType, value, value)})";
                case INamedTypeSymbol named when IsCollection(named, "List", 1):
                    return $"{Library}.Rlp.LengthOfSequence({PayloadOfItems(type, named.TypeArguments[0], value, value)})";
                case INamedTypeSymbol { IsTupleType: true } tuple:
                    return $"({Length(tuple.TupleElements[0].Type, null, $"{value}.Item1")} + {Length(tuple.TupleElements[1].Type, null, $"{value}.Item2")})";
                default:
                    string ns = type.ContainingNamespace.IsGlobalNamespace ? "global::" : $"global::{type.ContainingNamespace.ToDisplayString()}.";
                    return $"{ns}{type.Name}RlpConverter.GetLength({value})";
            }
        }

        private string PayloadOfItems(ITypeSymbol type, ITypeSymbol itemType, string member, string value)
        {
            string count = type is IArrayTypeSymbol ? "Length" : "Count";
            if (FixedLength(itemType, null) is { } itemLength) return $"{value}.{count} * {itemLength}";

            string name = $"PayloadOfItems{_helperCount++}";
            string length = Length(itemType, null, "item");
            Helpers.Add($$"""
                    private static int {{name}}({{Name(type)}} items)
                    {
                        int payloadLength = 0;
                        foreach ({{Name(itemType)}} item in items)
                        {
                            payloadLength += {{length}};
                        }

                        return payloadLength;
                    }

                """);
            return $"{name}({value})";
        }

        private string ReadLambda(ITypeSymbol type, string member)
            => $"static (scoped ref {Library}.RlpReader r) => {Read(type, member)}";

        private string WriteLambda(ITypeSymbol type, string member)
            => $"static (ref {Library}.RlpWriter w, {Name(type)} value) => {Write(type, member, "value")}";

        private static string Name(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        internal static bool IsInteger(ITypeSymbol type)
            => type.SpecialType is SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64
                   or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
               || type is INamedTypeSymbol { Name: "Int128" or "UInt128", ContainingNamespace.Name: "System", ContainingNamespace.ContainingNamespace.IsGlobalNamespace: true };

        // Other types use the Read{Name}() and Write(value) extension methods of a hand-written converter.
        private static bool IsConverterConvention(INamedTypeSymbol type)
            => type is { IsTupleType: false, IsGenericType: false, SpecialType: SpecialType.None, TypeKind: TypeKind.Class or TypeKind.Struct };

        // Array and list keys compare by reference, so equal keys would not be found or rejected as duplicates.
        private static bool HasValueEquality(ITypeSymbol key)
            => key is not IArrayTypeSymbol && !(key is INamedTypeSymbol named && IsCollection(named, "List", 1));

        internal static bool IsCollection(INamedTypeSymbol type, string name, int arity)
            => type.Name == name && type.Arity == arity && type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";
    }
}
