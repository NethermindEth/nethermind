// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal sealed record StageBControlTerm(string Kind, string Value, StageBControlTerm[] Arguments);
internal sealed record StageBControlParameter(string Name, string Type);
internal sealed record StageBControlMethod(string Name, string Result, bool Generic, StageBControlParameter[] Parameters, StageBControlTerm Body);
internal sealed record StageBControlDocument(int SchemaVersion, string ArtifactKind, StageBControlMethod[] Methods);
internal sealed record StageBControlInput(string Path, string Sha256);
internal sealed record StageBControlManifest(int SchemaVersion, string ArtifactKind, string Configuration,
    string[] Defines, StageBControlInput[] Sources, StageBControlInput[] References, StageBControlInput[] BuildFiles,
    StageBArtifactFile Ir, StageBArtifactFile Lean);
internal sealed record StageBControlCompilation(CSharpCompilation Compilation, string[] Defines,
    StageBControlInput[] Sources, StageBControlInput[] References, StageBControlInput[] BuildFiles);

internal static class StageBControlExtractor
{
    internal const string PackagePath = "tools/Evm/Lean/SimpleTransferCompletionExtractor";
    internal const string KernelPath = PackagePath + "/StageBControlKernel.cs";
    internal const string InterpreterPath = PackagePath + "/StageBPrefixInterpreter.cs";
    internal const string ArtifactKind = "stage-b-interpreter-control-kernel";
    internal const string IrName = "InterpreterControl.ir.json";
    internal const string LeanName = "InterpreterControl.lean";
    internal const string ManifestName = "InterpreterControl.source-manifest.json";
    private const string Namespace = "Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.";

    internal static StageBControlCompilation Load(string root)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using JsonDocument verification = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "tools/Evm/Lean/verification-manifest.json")));
        string revision = verification.RootElement.GetProperty("pins").GetProperty("nethermindCommit").GetString()!;
        start.Environment["SOURCE_DATE_EPOCH"] = CompilerReferences.SourceDateEpoch;
        foreach (string argument in new[]
        {
            "msbuild", PackagePath + "/SimpleTransferCompletionExtractor.csproj",
            "-t:ResolveReferences;GenerateAssemblyInfo;GenerateTargetFrameworkMonikerAttribute;GenerateGlobalUsings",
            "-p:Configuration=Release", "-p:BuildProjectReferences=false", "-p:SaveDiskSpace=true",
            "-p:SourceRevisionId=" + revision, "-p:SourceDateEpoch=" + CompilerReferences.SourceDateEpoch,
            "-p:EnableSourceLink=false", "-p:EmbedUntrackedSources=false", "-p:ContinuousIntegrationBuild=false",
            "-p:Deterministic=true", "-getItem:ReferencePath,Compile",
            "-getProperty:AssemblyName,DefineConstants,TargetFramework,LangVersion,AllowUnsafeBlocks,CheckForOverflowUnderflow,Nullable,OutputType,NuGetPackageRoot,NetCoreTargetingPackRoot",
            "-nr:false", "-m:1",
        }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new ExtractionException("Cannot resolve package Release compiler inputs.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ExtractionException("Package compiler inputs failed: " + output.Result + error.Result);
        using JsonDocument query = JsonDocument.Parse(output.Result);
        JsonElement properties = query.RootElement.GetProperty("Properties");
        string Property(string name) => properties.GetProperty(name).GetString()!;
        if (Property("AssemblyName") != Namespace.TrimEnd('.') || Property("TargetFramework") != "net10.0" ||
            Property("LangVersion") != "14.0" || Property("AllowUnsafeBlocks") != "false" ||
            Property("CheckForOverflowUnderflow") != "false" || Property("Nullable") != "enable" || Property("OutputType") != "Exe")
            throw new ExtractionException("Package Release compiler configuration changed.");
        string[] defines = Property("DefineConstants").Split(';');
        if (!defines.Contains("RELEASE") || defines.Contains("DEBUG")) throw new ExtractionException("Not a Release compilation.");
        (string Prefix, string Directory)[] roots =
        [
            ("repo/", Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
            ("nuget/", Path.GetFullPath(Property("NuGetPackageRoot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
            ("framework/", Path.GetFullPath(Property("NetCoreTargetingPackRoot")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
        ];
        string Identity(string path)
        {
            (string prefix, string directory) = roots.FirstOrDefault(item => path.StartsWith(item.Directory, StringComparison.OrdinalIgnoreCase));
            return prefix is null ? throw new ExtractionException("Compiler input outside closed roots: " + path) : prefix + path[directory.Length..].Replace('\\', '/');
        }
        JsonElement items = query.RootElement.GetProperty("Items");
        string[] Paths(string name) => items.GetProperty(name).EnumerateArray()
            .Select(static item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!)).Order(StringComparer.Ordinal).ToArray();
        string[] sources = Paths("Compile");
        string[] references = Paths("ReferencePath");
        StageBControlInput[] Inventory(IEnumerable<string> paths) => paths.Select(path => new StageBControlInput(Identity(path), CompilerReferences.Hash(File.ReadAllBytes(path))))
            .OrderBy(static input => input.Path, StringComparer.Ordinal).ToArray();
        CSharpParseOptions parse = new(LanguageVersion.CSharp14, preprocessorSymbols: defines);
        CSharpCompilation compilation = CSharpCompilation.Create(Property("AssemblyName"),
            sources.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parse, path, Encoding.UTF8)),
            references.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false, allowUnsafe: false, nullableContextOptions: NullableContextOptions.Enable, deterministic: true));
        RequireCompiles(compilation);
        string[] buildFiles = ["global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
            "tools/Directory.Build.props", "tools/Directory.Build.targets", PackagePath + "/SimpleTransferCompletionExtractor.csproj"];
        return new(compilation, defines, Inventory(sources), Inventory(references),
            Inventory(buildFiles.Select(path => Path.Combine(root, path)).Where(File.Exists)));
    }

    internal static void RequireCompiles(CSharpCompilation compilation)
    {
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new ExtractionException("Control compilation is invalid: " + string.Join("\n", errors.Select(static error => error.ToString())));
    }

    internal static StageBControlDocument Generate(CSharpCompilation compilation)
    {
        RequireCompiles(compilation);
        INamedTypeSymbol kernel = compilation.GetTypeByMetadataName(Namespace + "StageBControlKernel") ?? throw new ExtractionException("Missing control kernel.");
        ClassDeclarationSyntax declaration = Declaration(kernel) as ClassDeclarationSyntax
            ?? throw new ExtractionException("Control kernel is not a class.");
        RejectTypedReferenceIntrinsics(declaration);
        if (!kernel.IsStatic || declaration.ContainsDirectives || declaration.Members.Count != 5 ||
            declaration.Members.Any(static member => member is not MethodDeclarationSyntax) || kernel.GetAttributes().Length != 0)
            throw new ExtractionException("Control kernel shape changed.");
        AdmitResult(compilation, "StageBTickResult", "(boolExhausted,longRemainingFuel)");
        AdmitResult(compilation, "StageBEdgeResult", "(StageBEdgeKindKind,intDestination)");
        AdmitResult(compilation, "StageBBlockCursor`1", "(intOrdinal,TCarried)");
        AdmitResult(compilation, "StageBFinishResult`1", "(StageBEdgeKindKind,StageBBlockCursor<T>Cursor)");
        INamedTypeSymbol edgeKind = compilation.GetTypeByMetadataName(Namespace + "StageBEdgeKind") ?? throw new ExtractionException("Missing control result enum.");
        SyntaxNode enumSyntax = Declaration(edgeKind);
        if (Tokens(enumSyntax) != "internalenumStageBEdgeKind{Jump,Return,InvalidCondition,MissingEdge,InvalidEdge,MissingSuspension}")
            throw new ExtractionException("Edge result tags changed.");
        INamedTypeSymbol localReturnKind = compilation.GetTypeByMetadataName(Namespace + "StageBLocalReturnKind") ?? throw new ExtractionException("Missing local return enum.");
        if (Tokens(Declaration(localReturnKind)) != "internalenumStageBLocalReturnKind{ReturnedValue,ReceiverValue,ReceiverLocation}")
            throw new ExtractionException("Local return tags changed.");
        StageBControlMethod[] methods = declaration.Members.Cast<MethodDeclarationSyntax>().Select(method =>
        {
            SemanticModel model = compilation.GetSemanticModel(method.SyntaxTree);
            IMethodSymbol symbol = model.GetDeclaredSymbol(method)!;
            if (!symbol.IsStatic || symbol.GetAttributes().Length != 0 ||
                symbol.Parameters.Any(static parameter => parameter.RefKind != RefKind.None || parameter.IsOptional) ||
                (symbol.Name == "ShouldReadTransparent" ? method is not { Body: null, ExpressionBody: not null } :
                    method is not { Body: not null, ExpressionBody: null }))
                throw new ExtractionException("Unsupported control root signature.");
            string expected = symbol.Name switch
            {
                "Tick" => "internalstaticStageBTickResultTick(longremainingFuel)",
                "SelectEdge" => "internalstaticStageBEdgeResultSelectEdge(stringconditionKind,boolvalueIsBoolean,boolbooleanValue,boolhasFallThrough,intfallThroughDestination,boolfallThroughReturns,boolhasConditional,intconditionalDestination,boolconditionalReturns)",
                "FinishBlock" => "internalstaticStageBFinishResult<T>FinishBlock<T>(StageBPrefixExitexit,StageBBlockCursor<T>cursor,stringconditionKind,boolvalueIsBoolean,boolbooleanValue,boolhasFallThrough,intfallThroughDestination,boolfallThroughReturns,boolhasConditional,intconditionalDestination,boolconditionalReturns)",
                "SelectLocalReturn" => "internalstaticStageBLocalReturnKindSelectLocalReturn(boolconstructing,boolvalueMode)",
                "ShouldReadTransparent" => "internalstaticboolShouldReadTransparent(boolpreservesOperand,boolvalueMode)",
                _ => throw new ExtractionException("Unknown control root."),
            };
            if (Tokens(method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)) != expected)
                throw new ExtractionException("Control root signature drift.");
            StageBControlTerm body = method.Body is { } block
                ? Statements(((IBlockOperation)model.GetOperation(block)!).Operations.ToArray(), 0)
                : Expression(model.GetOperation(method.ExpressionBody!.Expression)!);
            return new StageBControlMethod(symbol.Name, Type(symbol.ReturnType), symbol.IsGenericMethod,
                symbol.Parameters.Select(static parameter => new StageBControlParameter(parameter.Name, Type(parameter.Type))).ToArray(),
                body);
        }).ToArray();
        if (!methods.Select(static method => method.Name).SequenceEqual(new[] { "Tick", "SelectEdge", "FinishBlock", "SelectLocalReturn", "ShouldReadTransparent" }))
            throw new ExtractionException("Control root order changed.");
        AdmitAdapters(compilation, kernel);
        return new(2, ArtifactKind, methods);
    }

    private static void AdmitResult(CSharpCompilation compilation, string name, string parameters)
    {
        INamedTypeSymbol symbol = compilation.GetTypeByMetadataName(Namespace + name) ?? throw new ExtractionException("Missing control result.");
        string syntaxName = symbol.Name + (symbol.IsGenericType ? "<T>" : "");
        if (Declaration(symbol) is not RecordDeclarationSyntax record ||
            Tokens(record) != "internalreadonlyrecordstruct" + syntaxName + parameters + ";")
            throw new ExtractionException("Control result representation changed.");
        ITypeSymbol[] types = name switch
        {
            "StageBTickResult" => [compilation.GetSpecialType(SpecialType.System_Boolean), compilation.GetSpecialType(SpecialType.System_Int64)],
            "StageBEdgeResult" => [compilation.GetTypeByMetadataName(Namespace + "StageBEdgeKind")!, compilation.GetSpecialType(SpecialType.System_Int32)],
            "StageBBlockCursor`1" => [compilation.GetSpecialType(SpecialType.System_Int32), symbol.TypeParameters[0]],
            "StageBFinishResult`1" => [compilation.GetTypeByMetadataName(Namespace + "StageBEdgeKind")!,
                compilation.GetTypeByMetadataName(Namespace + "StageBBlockCursor`1")!.Construct(symbol.TypeParameters[0])],
            _ => throw new ExtractionException("Unknown control result."),
        };
        string[] names = name switch
        {
            "StageBTickResult" => ["Exhausted", "RemainingFuel"], "StageBEdgeResult" => ["Kind", "Destination"],
            "StageBBlockCursor`1" => ["Ordinal", "Carried"], _ => ["Kind", "Cursor"],
        };
        IMethodSymbol constructor = One(symbol.InstanceConstructors.Where(static constructor => constructor.Parameters.Length == 2), "Control result constructor changed.");
        for (int index = 0; index < types.Length; index++)
        {
            IPropertySymbol property = One(symbol.GetMembers(names[index]).OfType<IPropertySymbol>(), "Control result property changed.");
            if (!SymbolEqualityComparer.Default.Equals(property.Type, types[index]) ||
                !SymbolEqualityComparer.Default.Equals(constructor.Parameters[index].Type, types[index]) ||
                constructor.Parameters[index].Name != names[index] || property.IsStatic || property.GetMethod is null ||
                Declaration(property) is not ParameterSyntax parameter ||
                parameter.Span != record.ParameterList!.Parameters[index].Span)
                throw new ExtractionException("Control result positional member rebound.");
        }
    }

    private static StageBControlTerm Statements(IOperation[] operations, int index)
    {
        if (index >= operations.Length) throw new ExtractionException("Control path has no return.");
        switch (operations[index])
        {
            case IReturnOperation { ReturnedValue: { } result } when index == operations.Length - 1:
                return Expression(result);
            case IConditionalOperation { WhenTrue: IReturnOperation { ReturnedValue: { } result }, WhenFalse: null } conditional:
                return new("if", "", [Expression(conditional.Condition), Expression(result), Statements(operations, index + 1)]);
            case IVariableDeclarationGroupOperation group when group.Declarations.Length == 1 && group.Declarations[0].Declarators.Length == 1:
                IVariableDeclaratorOperation local = group.Declarations[0].Declarators[0];
                if (local.Initializer is null) throw new ExtractionException("Uninitialized control local.");
                return new("let", local.Symbol.Name, [Expression(local.Initializer.Value), Statements(operations, index + 1)]);
            default:
                throw new ExtractionException("Unsupported control statement: " + operations[index].Kind);
        }
    }

    private static StageBControlTerm Expression(IOperation operation)
    {
        static StageBControlTerm Term(string kind, string value, params StageBControlTerm[] arguments) => new(kind, value, arguments);
        switch (operation)
        {
            case ILiteralOperation literal when literal.ConstantValue is { HasValue: true, Value: not null }:
                return Term("literal", Type(literal.Type!) + ":" + Convert.ToString(literal.ConstantValue.Value, CultureInfo.InvariantCulture));
            case IParameterReferenceOperation parameter:
                return Term("variable", parameter.Parameter.Name);
            case ILocalReferenceOperation local:
                return Term("variable", local.Local.Name);
            case IFieldReferenceOperation field when field.Field.ContainingType.ToDisplayString() is Namespace + "StageBEdgeKind" or Namespace + "StageBPrefixExit" or Namespace + "StageBLocalReturnKind" && field.Field.HasConstantValue:
                return Term("literal", "Int:" + Convert.ToString(field.Field.ConstantValue, CultureInfo.InvariantCulture));
            case IPropertyReferenceOperation property when property.Instance is not null && property.Arguments.Length == 0 &&
                property.Property.ContainingType.OriginalDefinition.ToDisplayString() is Namespace + "StageBEdgeResult" or Namespace + "StageBBlockCursor<T>":
                return Term("property", char.ToLowerInvariant(property.Property.Name[0]) + property.Property.Name[1..], Expression(property.Instance));
            case IInvocationOperation invocation when invocation.Instance is null &&
                invocation.TargetMethod.ContainingType.ToDisplayString() == Namespace + "StageBControlKernel" && invocation.TargetMethod.Name == "SelectEdge" &&
                invocation.Arguments.Select(static argument => argument.Parameter?.Ordinal).SequenceEqual(Enumerable.Range(0, 9).Select(static index => (int?)index)):
                return Term("call", "SelectEdge", invocation.Arguments.Select(static argument => Expression(argument.Value)).ToArray());
            case IConversionOperation conversion when conversion.OperatorMethod is null && !conversion.IsChecked &&
                (SymbolEqualityComparer.Default.Equals(conversion.Operand.Type, conversion.Type) ||
                 conversion.Operand.Type?.SpecialType == SpecialType.System_Int32 && conversion.Type?.SpecialType == SpecialType.System_Int64):
                return Expression(conversion.Operand);
            case IUnaryOperation unary when unary.OperatorMethod is null && !unary.IsChecked &&
                (unary.OperatorKind == UnaryOperatorKind.Not && unary.Type?.SpecialType == SpecialType.System_Boolean ||
                 unary.OperatorKind == UnaryOperatorKind.Minus && unary.Operand.ConstantValue is { HasValue: true, Value: int and >= 0 } && unary.Type?.SpecialType == SpecialType.System_Int32):
                return Term(unary.OperatorKind == UnaryOperatorKind.Not ? "not" : "neg", "", Expression(unary.Operand));
            case IBinaryOperation binary when binary.OperatorMethod is null && !binary.IsChecked:
                string kind = binary.OperatorKind switch
                {
                    BinaryOperatorKind.Equals => "eq", BinaryOperatorKind.NotEquals => "ne", BinaryOperatorKind.LessThan => "lt",
                    BinaryOperatorKind.ConditionalAnd => "and", BinaryOperatorKind.ConditionalOr => "or",
                    BinaryOperatorKind.Subtract when binary.Type?.SpecialType == SpecialType.System_Int64 => "sub64",
                    _ => throw new ExtractionException("Unsupported control binary operator."),
                };
                return Term(kind, "", Expression(binary.LeftOperand), Expression(binary.RightOperand));
            case IConditionalOperation conditional when conditional.WhenFalse is not null:
                return Term("if", "", Expression(conditional.Condition), Expression(conditional.WhenTrue), Expression(conditional.WhenFalse));
            case IObjectCreationOperation creation when creation.Constructor?.ContainingType.OriginalDefinition.ToDisplayString() is
                    Namespace + "StageBTickResult" or Namespace + "StageBEdgeResult" or Namespace + "StageBBlockCursor<T>" or Namespace + "StageBFinishResult<T>" &&
                creation.Initializer is null && creation.Arguments.Length == 2 &&
                creation.Arguments.Select(static argument => argument.Parameter?.Ordinal).SequenceEqual(new int?[] { 0, 1 }):
                return Term("construct", Type(creation.Type!).Replace(" α", "", StringComparison.Ordinal), creation.Arguments.Select(static argument => Expression(argument.Value)).ToArray());
            default:
                throw new ExtractionException("Unsupported control expression: " + operation.Kind + " / " + operation.Syntax);
        }
    }

    private static string Type(ITypeSymbol type) => type.ToDisplayString() switch
    {
        "bool" => "Bool", "long" or "int" or Namespace + "StageBEdgeKind" or Namespace + "StageBPrefixExit" or Namespace + "StageBLocalReturnKind" => "Int", "string" => "String",
        Namespace + "StageBTickResult" => "TickResult", Namespace + "StageBEdgeResult" => "EdgeResult",
        Namespace + "StageBBlockCursor<T>" => "BlockCursor α", Namespace + "StageBFinishResult<T>" => "FinishResult α",
        _ => throw new ExtractionException("Unsupported control type: " + type),
    };

    internal static string Emit(StageBControlDocument document)
    {
        StringBuilder lean = new("-- Generated from admitted Roslyn operations; do not edit.\nnamespace SimpleTransferCompletionExtractor.StageB.Control.Generated\n\nstructure TickResult where\n  exhausted : Bool\n  remainingFuel : Int\n  deriving DecidableEq, Repr\n\nstructure EdgeResult where\n  kind : Int\n  destination : Int\n  deriving DecidableEq, Repr\n\ndef wrap64 (value : Int) : Int := (value + 9223372036854775808) % 18446744073709551616 - 9223372036854775808\n\n");
        lean.Append("structure BlockCursor (α : Type) where\n  ordinal : Int\n  carried : α\n  deriving DecidableEq, Repr\n\nstructure FinishResult (α : Type) where\n  kind : Int\n  cursor : BlockCursor α\n  deriving DecidableEq, Repr\n\n");
        foreach (StageBControlMethod method in document.Methods)
        {
            lean.Append("def ").Append(method.Name).Append(' ');
            if (method.Generic) lean.Append("{α : Type} ");
            foreach (StageBControlParameter parameter in method.Parameters) lean.Append('(').Append(parameter.Name).Append(" : ").Append(parameter.Type).Append(") ");
            lean.Append(": ").Append(method.Result).Append(" :=\n  ").Append(Render(method.Body)).Append("\n\n");
        }
        return lean.Append("end SimpleTransferCompletionExtractor.StageB.Control.Generated\n").ToString();
    }

    private static string Render(StageBControlTerm term)
    {
        string A(int index) => Render(term.Arguments[index]);
        return term.Kind switch
        {
            "literal" when term.Value.StartsWith("Bool:", StringComparison.Ordinal) => term.Value[5..].ToLowerInvariant(),
            "literal" when term.Value.StartsWith("String:", StringComparison.Ordinal) => JsonSerializer.Serialize(term.Value[7..]),
            "literal" when term.Value.StartsWith("Int:", StringComparison.Ordinal) => "(" + term.Value[4..] + " : Int)",
            "variable" => term.Value,
            "let" => "(let " + term.Value + " := " + A(0) + "; " + A(1) + ")",
            "if" => "(if " + A(0) + " then " + A(1) + " else " + A(2) + ")",
            "construct" => "(" + term.Value + ".mk " + A(0) + " " + A(1) + ")",
            "property" => "(" + A(0) + ")." + term.Value,
            "call" => "(" + term.Value + " " + string.Join(" ", term.Arguments.Select(Render)) + ")",
            "not" => "(!" + A(0) + ")", "neg" => "(-" + A(0) + ")",
            "eq" => "(" + A(0) + " == " + A(1) + ")", "ne" => "(" + A(0) + " != " + A(1) + ")",
            "lt" => "(decide (" + A(0) + " < " + A(1) + "))",
            "and" => "(" + A(0) + " && " + A(1) + ")", "or" => "(" + A(0) + " || " + A(1) + ")",
            "sub64" => "(wrap64 (" + A(0) + " - " + A(1) + "))",
            _ => throw new ExtractionException("Invalid control IR node: " + term.Kind),
        };
    }

    internal static Dictionary<string, byte[]> RenderArtifacts(StageBControlCompilation inputs)
    {
        StageBControlDocument document = Generate(inputs.Compilation);
        byte[] ir = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, CompilerReferences.JsonOptions) + "\n");
        byte[] lean = Encoding.UTF8.GetBytes(Emit(document));
        StageBControlManifest manifest = new(2, ArtifactKind, "Release", inputs.Defines, inputs.Sources, inputs.References, inputs.BuildFiles,
            new(IrName, CompilerReferences.Hash(ir)), new(LeanName, CompilerReferences.Hash(lean)));
        byte[] metadata = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, CompilerReferences.JsonOptions) + "\n");
        return new(StringComparer.Ordinal) { [IrName] = ir, [LeanName] = lean, [ManifestName] = metadata };
    }

    internal static void ValidateArtifacts(IReadOnlyDictionary<string, byte[]> supplied, IReadOnlyDictionary<string, byte[]> fresh)
    {
        if (!supplied.Keys.Order(StringComparer.Ordinal).SequenceEqual(fresh.Keys.Order(StringComparer.Ordinal)))
            throw new ExtractionException("Control artifact roster changed.");
        foreach ((string name, byte[] bytes) in fresh)
            if (!supplied[name].AsSpan().SequenceEqual(bytes)) throw new ExtractionException("Control artifact is not fresh/canonical: " + name);
    }

    internal static void Extract(string root, string output, bool check)
    {
        Dictionary<string, byte[]> artifacts = RenderArtifacts(Load(root));
        if (check)
        {
            ValidateDirectory(output, artifacts);
            return;
        }
        Directory.CreateDirectory(output);
        foreach ((string name, byte[] bytes) in artifacts) File.WriteAllBytes(Path.Combine(output, name), bytes);
    }

    internal static void ValidateDirectory(string output, IReadOnlyDictionary<string, byte[]> fresh) =>
        ValidateArtifacts(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(output, path).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal), fresh);

    private static string Tokens(SyntaxNode syntax) => string.Concat(syntax.DescendantTokens().Select(static token => token.Text));

    private static T One<T>(IEnumerable<T> candidates, string message)
    {
        T[] values = candidates.Take(2).ToArray();
        return values.Length == 1 ? values[0] : throw new ExtractionException(message);
    }

    private static SyntaxNode Declaration(ISymbol symbol) =>
        One(symbol.DeclaringSyntaxReferences, "Control declaration cardinality changed: " + symbol.Name).GetSyntax();

    private static void RejectTypedReferenceIntrinsics(SyntaxNode declaration)
    {
        if (declaration.DescendantNodesAndSelf().Any(static node => node.Kind() is
                SyntaxKind.MakeRefExpression or SyntaxKind.RefValueExpression or SyntaxKind.RefTypeExpression or SyntaxKind.ArgListExpression) ||
            declaration.DescendantTokens().Any(static token => token.IsKind(SyntaxKind.ArgListKeyword)))
            throw new ExtractionException("Control TypedReference intrinsics are unsupported.");
    }

    private static string SourceCallRoster(SemanticModel model, SyntaxNode declaration, Action<IMethodSymbol>? admitTarget = null)
    {
        Dictionary<string, int> calls = new(StringComparer.Ordinal);
        HashSet<(int Start, int Length, string Role, string Target)> seen = [];
        HashSet<IOperation> roots = new(ReferenceEqualityComparer.Instance);
        foreach (SyntaxNode node in declaration.DescendantNodes())
        {
            IOperation? root = model.GetOperation(node);
            if (root is null) continue;
            while (root.Parent is not null) root = root.Parent;
            roots.Add(root);
        }
        foreach (IOperation root in roots)
        foreach (IOperation operation in root.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IInvocationOperation invocation: Add(operation, "invoke", invocation.TargetMethod); break;
                case IObjectCreationOperation creation: Add(operation, "new", creation.Constructor); break;
                case IMethodReferenceOperation reference: Add(operation, "method-reference", reference.Method); break;
                case IBinaryOperation binary: Add(operation, "binary", binary.OperatorMethod); break;
                case IUnaryOperation unary: Add(operation, "unary", unary.OperatorMethod); break;
                case IConversionOperation conversion: Add(operation, "conversion", conversion.OperatorMethod); break;
                case IArgumentOperation argument:
                    Add(operation, "argument-in-conversion", argument.InConversion.MethodSymbol);
                    Add(operation, "argument-out-conversion", argument.OutConversion.MethodSymbol);
                    break;
                case ICompoundAssignmentOperation assignment:
                    Add(operation, "compound", assignment.OperatorMethod);
                    Add(operation, "compound-in-conversion", assignment.InConversion.MethodSymbol);
                    Add(operation, "compound-out-conversion", assignment.OutConversion.MethodSymbol);
                    break;
                case ICoalesceOperation coalesce: Add(operation, "coalesce-conversion", coalesce.ValueConversion.MethodSymbol); break;
                case IIncrementOrDecrementOperation increment: Add(operation, "increment", increment.OperatorMethod); break;
                case IPropertyReferenceOperation property:
                    bool writes = property.Parent is IAssignmentOperation parent && parent.Target == property ||
                        property.Parent is IIncrementOrDecrementOperation mutation && mutation.Target == property;
                    if (!writes || property.Parent is ICompoundAssignmentOperation or IIncrementOrDecrementOperation) Add(operation, "get", property.Property.GetMethod);
                    if (writes) Add(operation, "set", property.Property.SetMethod);
                    break;
            }
        }
        return string.Join('\n', calls.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Value.ToString(CultureInfo.InvariantCulture) + " " + pair.Key));

        void Add(IOperation operation, string role, IMethodSymbol? target)
        {
            if (target is null) return;
            admitTarget?.Invoke(target);
            if (!SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, model.Compilation.Assembly)) return;
            target = target.ReducedFrom ?? target;
            string targetId = target.OriginalDefinition.GetDocumentationCommentId() ?? throw new ExtractionException("Control source target has no identity.");
            if (!seen.Add((operation.Syntax.Span.Start, operation.Syntax.Span.Length, role, targetId))) return;
            string identity = role + " | " + targetId.Replace(Namespace, "$", StringComparison.Ordinal);
            calls[identity] = calls.GetValueOrDefault(identity) + 1;
        }
    }

    private static void AdmitRuntimeRepresentations(CSharpCompilation compilation)
    {
        (string Name, string Sha256)[] representations =
        [
            ("StageBPrefixInterpreter+Cell", "9aad0637172717b682f8d8829bd6f011df56696b858fc3592a02665561374a13"),
            ("StageBPrefixInterpreter+Operand", "554fbb0dd590946e4dd4d58c6e83f24ad55e34cffc99b0e9df32080fe8d32f46"),
            ("StageBPrefixInterpreter+Location", "527b9914b862d58076b959133b219cf9bba02cec7d71a32c140612736d1f7b77"),
            ("StageBPrefixInterpreter+CellLocation", "50643cb9360917d1a5b3a418bfd57446618154df4e6e0323b5c410d94093608a"),
            ("StageBPrefixInterpreter+FieldLocation", "b2295afa116c886248ac469374d929e862caf7321bddac5e25092a83d00cae33"),
            ("StageBPrefixInterpreter+ValueLocation", "7bef306459845da0a04c4836595d75ba0d2b62326f5cf4bfc0c984118adb6948"),
            ("StageBPrefixInterpreter+DiscardLocation", "a77826be90cea5139122a15bc6ab3d99c6e65d9d05c79ed6401d7acb575a9cd7"),
            ("StageBResolvedLocation", "5437c1fa23783e9f197edcd8ddc807939f5fbfa0dc3c9629250acd62277c1f1d"),
            ("StageBValue", "1660f0a35c59f51e820da5c5c534743dde047da4e94222a89aa1490f1b5d0f0a"),
        ];
        Dictionary<string, INamedTypeSymbol> types = new(StringComparer.Ordinal);
        foreach (string name in representations.Select(static representation => Namespace + representation.Name).Concat(
            [Namespace + "StageBExecutionException", Namespace + "StageBValueKind",
                "System.Object", "System.String", "System.StringComparer", "System.ArgumentOutOfRangeException",
                "System.Numerics.BigInteger", "System.Linq.Enumerable", "System.MemoryExtensions",
                "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.IReadOnlyDictionary`2",
                "System.Collections.Generic.IReadOnlyCollection`1", "System.Collections.Generic.KeyValuePair`2"]))
        {
            INamedTypeSymbol type = compilation.GetTypeByMetadataName(name)
                ?? throw new ExtractionException("Control runtime representation type missing: " + name);
            if (name.StartsWith("System.", StringComparison.Ordinal) && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                throw new ExtractionException("Control runtime representation binding changed: " + type.Name);
            types.Add(type.Name, type);
        }
        HashSet<ISymbol> admittedTypes = new(types.Values, SymbolEqualityComparer.Default);
        foreach ((string name, string expectedHash) in representations)
        {
            INamedTypeSymbol type = compilation.GetTypeByMetadataName(Namespace + name)!;
            if (type.DeclaringSyntaxReferences.Length != 1)
                throw new ExtractionException("Control runtime representation changed: " + type.Name);
            SyntaxNode declaration = Declaration(type);
            string tokens = string.Concat(declaration.DescendantTokens().Select(static token =>
                string.Create(CultureInfo.InvariantCulture, $"{token.RawKind}:{token.Text.Length}:{token.Text}")));
            if (CompilerReferences.Hash(Encoding.UTF8.GetBytes(tokens)) != expectedHash)
                throw new ExtractionException("Control runtime representation changed: " + type.Name);
            SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (SimpleNameSyntax identifier in declaration.DescendantNodes().OfType<SimpleNameSyntax>())
                if (types.TryGetValue(identifier.Identifier.ValueText, out INamedTypeSymbol? expected) &&
                    model.GetSymbolInfo(identifier).Symbol is INamedTypeSymbol actual &&
                    !SymbolEqualityComparer.Default.Equals(actual.OriginalDefinition, expected))
                    throw new ExtractionException("Control runtime representation binding changed: " + identifier.Identifier.ValueText);
            _ = SourceCallRoster(model, declaration, target =>
            {
                if (!admittedTypes.Contains(target.ContainingType.OriginalDefinition))
                    throw new ExtractionException("Control runtime representation binding changed: " + target.ToDisplayString());
            });
        }
    }

    // '$' expands to the package namespace; counts bind operation target slots, not callee semantics.
    private const string ExpectedMachineSourceCalls = """
        3 get | M:$StageBBinding.get_Symbol
        2 get | M:$StageBBlockCursor`1.get_Carried
        4 get | M:$StageBBlockCursor`1.get_Ordinal
        2 get | M:$StageBBoolExchange.get_Value
        1 get | M:$StageBCfgPoint.get_Block
        2 get | M:$StageBCodeLookupExchange.get_DelegationAddress
        1 get | M:$StageBCodeLookupExchange.get_IsEmpty
        2 get | M:$StageBEdge.get_Destination
        2 get | M:$StageBEdge.get_Semantics
        1 get | M:$StageBExecutionException.get_Rejection
        2 get | M:$StageBFinishResult`1.get_Cursor
        4 get | M:$StageBFinishResult`1.get_Kind
        4 get | M:$StageBMember.get_DeclaringType
        2 get | M:$StageBMember.get_Kind
        21 get | M:$StageBMember.get_Name
        8 get | M:$StageBMember.get_Parameters
        7 get | M:$StageBMember.get_Symbol
        10 get | M:$StageBMember.get_Type
        3 get | M:$StageBParameter.get_Name
        2 get | M:$StageBParameter.get_RefKind
        9 get | M:$StageBParameter.get_Symbol
        7 get | M:$StageBParameter.get_Type
        1 get | M:$StageBPostNonceInput.get_BlobBaseFee
        1 get | M:$StageBPostNonceInput.get_Commit
        1 get | M:$StageBPostNonceInput.get_DeleteCallerAccount
        1 get | M:$StageBPostNonceInput.get_ExecutionGasLimitCap
        1 get | M:$StageBPostNonceInput.get_ForceSimpleTransferDisabled
        1 get | M:$StageBPostNonceInput.get_IntrinsicGas
        1 get | M:$StageBPostNonceInput.get_IsCodeOverridable
        1 get | M:$StageBPostNonceInput.get_NewAccountStateCost
        1 get | M:$StageBPostNonceInput.get_OpcodeGasPrice
        1 get | M:$StageBPostNonceInput.get_PremiumPerGas
        1 get | M:$StageBPostNonceInput.get_Restore
        1 get | M:$StageBPostNonceInput.get_SenderReservedGasPayment
        2 get | M:$StageBPostNonceInput.get_Spec
        5 get | M:$StageBPostNonceInput.get_Tracer
        7 get | M:$StageBPostNonceInput.get_Transaction
        1 get | M:$StageBPostNonceInput.get_Warmup
        4 get | M:$StageBPrefixArgument.get_Child
        3 get | M:$StageBPrefixArgument.get_Mode
        5 get | M:$StageBPrefixArgument.get_Ordinal
        1 get | M:$StageBPrefixBlock.get_BranchValue
        1 get | M:$StageBPrefixBlock.get_ConditionKind
        3 get | M:$StageBPrefixBlock.get_Conditional
        2 get | M:$StageBPrefixBlock.get_Exit
        3 get | M:$StageBPrefixBlock.get_FallThrough
        1 get | M:$StageBPrefixBlock.get_Operations
        1 get | M:$StageBPrefixBlock.get_Ordinal
        7 get | M:$StageBPrefixCall.get_Arguments
        4 get | M:$StageBPrefixCall.get_ReceiverChild
        25 get | M:$StageBPrefixCall.get_Target
        1 get | M:$StageBPrefixCaptureMode.get_Capture
        1 get | M:$StageBPrefixCaptureMode.get_Mode
        1 get | M:$StageBPrefixFunction.get_Bindings
        1 get | M:$StageBPrefixFunction.get_Blocks
        1 get | M:$StageBPrefixFunction.get_CaptureModes
        1 get | M:$StageBPrefixFunction.get_Entry
        11 get | M:$StageBPrefixFunction.get_Signature
        1 get | M:$StageBPrefixInterpreter.CellLocation.get_Cell
        2 get | M:$StageBPrefixInterpreter.Frame.get_Captures
        7 get | M:$StageBPrefixInterpreter.Frame.get_Cells
        6 get | M:$StageBPrefixInterpreter.Frame.get_Function
        2 get | M:$StageBPrefixInterpreter.Frame.get_This
        1 get | M:$StageBPrefixInterpreter.Location.get_Identity
        8 get | M:$StageBPrefixInterpreter.Operand.get_Target
        4 get | M:$StageBPrefixInterpreter.SuspendSignal.get_Operands
        6 get | M:$StageBPrefixNode.get_Binding
        12 get | M:$StageBPrefixNode.get_Call
        29 get | M:$StageBPrefixNode.get_Children
        3 get | M:$StageBPrefixNode.get_Constant
        11 get | M:$StageBPrefixNode.get_Kind
        12 get | M:$StageBPrefixNode.get_Mode
        6 get | M:$StageBPrefixNode.get_Operator
        9 get | M:$StageBPrefixNode.get_Symbol
        5 get | M:$StageBPrefixNode.get_Type
        1 get | M:$StageBPrefixProgram.get_Entry
        1 get | M:$StageBPrefixProgram.get_Functions
        2 get | M:$StageBPrefixTarget.get_Body
        9 get | M:$StageBPrefixTarget.get_Kind
        14 get | M:$StageBPrefixTarget.get_Member
        3 get | M:$StageBRefundOperand.get_Ordinal
        3 get | M:$StageBRefundOperand.get_Value
        1 get | M:$StageBSpecInput.get_IsEip7708Enabled
        1 get | M:$StageBSpecInput.get_IsEip8037Enabled
        5 get | M:$StageBTermBinding.get_Capture
        1 get | M:$StageBTermBinding.get_IsRef
        1 get | M:$StageBTickResult.get_Exhausted
        1 get | M:$StageBTickResult.get_RemainingFuel
        1 get | M:$StageBTracerInput.get_IsTracingAccess
        1 get | M:$StageBTracerInput.get_IsTracingActions
        1 get | M:$StageBTracerInput.get_IsTracingCode
        1 get | M:$StageBTracerInput.get_IsTracingLogs
        1 get | M:$StageBTracerInput.get_IsTracingState
        1 get | M:$StageBTransactionInput.get_Data
        1 get | M:$StageBTransactionInput.get_GasLimit
        1 get | M:$StageBTransactionInput.get_HasAuthorizationList
        1 get | M:$StageBTransactionInput.get_Recipient
        1 get | M:$StageBTransactionInput.get_Sender
        2 get | M:$StageBTransactionInput.get_Value
        2 get | M:$StageBValue.get_Boolean
        1 get | M:$StageBValue.get_Fields
        2 get | M:$StageBValue.get_Integer
        18 get | M:$StageBValue.get_Kind
        5 get | M:$StageBValue.get_Signed
        4 get | M:$StageBValue.get_Text
        9 get | M:$StageBValue.get_Type
        9 get | M:$StageBValue.get_Unit
        1 get | M:$StageBValue.get_Unsigned
        1 invoke | M:$StageBControlKernel.FinishBlock``1($StageBPrefixExit,$StageBBlockCursor{``0},System.String,System.Boolean,System.Boolean,System.Boolean,System.Int32,System.Boolean,System.Boolean,System.Int32,System.Boolean)
        1 invoke | M:$StageBControlKernel.SelectLocalReturn(System.Boolean,System.Boolean)
        1 invoke | M:$StageBControlKernel.ShouldReadTransparent(System.Boolean,System.Boolean)
        1 invoke | M:$StageBControlKernel.Tick(System.Int64)
        1 invoke | M:$StageBGasValue.ToValue
        1 invoke | M:$StageBIntrinsicGasInput.ToValue
        1 invoke | M:$StageBPrefixInterpreter.Location.Resolve
        4 invoke | M:$StageBPrefixInterpreter.Machine.Address($StageBValue)
        3 invoke | M:$StageBPrefixInterpreter.Machine.Arguments($StageBPrefixCall,$StageBPrefixInterpreter.Operand[])
        1 invoke | M:$StageBPrefixInterpreter.Machine.Assign($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Binary($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Binding($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        5 invoke | M:$StageBPrefixInterpreter.Machine.Boolean($StageBValue)
        5 invoke | M:$StageBPrefixInterpreter.Machine.Call($StageBPrefixInterpreter.Frame,$StageBPrefixNode,$StageBPrefixCall,$StageBPrefixInterpreter.Operand[],System.Boolean)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Capture($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Charge($StageBPrefixInterpreter.Operand[],$StageBPrefixCall)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Collection($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Constant(System.String,System.String)
        1 invoke | M:$StageBPrefixInterpreter.Machine.ConvertValue($StageBValue,System.String)
        5 invoke | M:$StageBPrefixInterpreter.Machine.Default(System.String)
        16 invoke | M:$StageBPrefixInterpreter.Machine.Evaluate($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        1 invoke | M:$StageBPrefixInterpreter.Machine.EvaluateTransparent($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        3 invoke | M:$StageBPrefixInterpreter.Machine.Execute($StageBPrefixInterpreter.Frame)
        1 invoke | M:$StageBPrefixInterpreter.Machine.External($StageBPrefixInterpreter.Frame,$StageBPrefixNode,$StageBPrefixCall,$StageBPrefixInterpreter.Operand[])
        1 invoke | M:$StageBPrefixInterpreter.Machine.Gas($StageBValue)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Initialize($StageBPrefixInterpreter.Operand[],$StageBPrefixCall)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Instance($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Invoke($StageBPrefixInterpreter.Frame,$StageBPrefixNode,System.Boolean)
        1 invoke | M:$StageBPrefixInterpreter.Machine.IsPattern($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        1 invoke | M:$StageBPrefixInterpreter.Machine.LocalName(System.String)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Member($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Pattern($StageBPrefixInterpreter.Frame,$StageBValue,$StageBPrefixNode)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Projection($StageBMember,$StageBValue,System.String)
        6 invoke | M:$StageBPrefixInterpreter.Machine.Request``1($StageBRequest)
        1 invoke | M:$StageBPrefixInterpreter.Machine.RootFrame($StageBPrefixFunction)
        12 invoke | M:$StageBPrefixInterpreter.Machine.Signed($StageBValue)
        2 invoke | M:$StageBPrefixInterpreter.Machine.Tick
        4 invoke | M:$StageBPrefixInterpreter.Machine.UInt256($StageBValue)
        1 invoke | M:$StageBPrefixInterpreter.Machine.Unary($StageBPrefixInterpreter.Frame,$StageBPrefixNode)
        5 invoke | M:$StageBPrefixInterpreter.Machine.Unsigned($StageBValue)
        3 invoke | M:$StageBPrefixInterpreter.Machine.ValueEquals($StageBValue,$StageBValue)
        1 invoke | M:$StageBPrefixInterpreter.Machine.WriteArgument($StageBPrefixCall,$StageBPrefixInterpreter.Operand[],System.Int32,$StageBValue)
        7 invoke | M:$StageBPrefixInterpreter.Operand.Location($StageBPrefixInterpreter.Location)
        28 invoke | M:$StageBPrefixInterpreter.Operand.Read
        36 invoke | M:$StageBPrefixInterpreter.Operand.Value($StageBValue)
        2 invoke | M:$StageBPrefixInterpreter.Operand.Write($StageBValue)
        1 invoke | M:$StageBResponseTape.Take``1($StageBRequest)
        3 invoke | M:$StageBValue.Address(System.String)
        26 invoke | M:$StageBValue.Bool(System.Boolean)
        3 invoke | M:$StageBValue.BytesValue(System.Byte[])
        10 invoke | M:$StageBValue.Enum(System.String,System.Int64)
        8 invoke | M:$StageBValue.Field(System.String)
        13 invoke | M:$StageBValue.Int64(System.Int64)
        8 invoke | M:$StageBValue.Null(System.String)
        12 invoke | M:$StageBValue.Reference(System.String,System.String)
        7 invoke | M:$StageBValue.Struct(System.String,System.ValueTuple{System.String,$StageBValue}[])
        1 invoke | M:$StageBValue.StructurallyEquals($StageBValue)
        9 invoke | M:$StageBValue.UInt256(System.Numerics.BigInteger)
        7 invoke | M:$StageBValue.UInt64(System.UInt64)
        1 invoke | M:Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge(System.UInt64,System.Int64,System.Int64,System.Int64,System.Int64,System.Int64)
        1 invoke | M:Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel.TryCreate(System.UInt64,System.UInt64,System.Int64,System.Boolean,System.UInt64)
        1 new | M:$StageBAddBalanceRequest.#ctor(System.String,System.Numerics.BigInteger)
        2 new | M:$StageBBlockCursor`1.#ctor(System.Int32,`0)
        1 new | M:$StageBCodeLookupRequest.#ctor(System.String,System.Boolean)
        1 new | M:$StageBCommitRequest.#ctor(System.Boolean,System.Boolean)
        35 new | M:$StageBExecutionException.#ctor(System.String,System.String,$StageBRejection)
        2 new | M:$StageBGasValue.#ctor(System.UInt64,System.Int64,System.Int64,System.Int64,System.Int64)
        1 new | M:$StageBIsDeadAccountRequest.#ctor(System.String)
        9 new | M:$StageBPrefixInterpreter.Cell.#ctor(System.String,$StageBValue)
        4 new | M:$StageBPrefixInterpreter.CellLocation.#ctor($StageBPrefixInterpreter.Cell,System.Boolean)
        1 new | M:$StageBPrefixInterpreter.DiscardLocation.#ctor(System.String)
        1 new | M:$StageBPrefixInterpreter.FieldLocation.#ctor($StageBPrefixInterpreter.Location,System.String,System.Boolean)
        3 new | M:$StageBPrefixInterpreter.Frame.#ctor($StageBPrefixFunction,$StageBPrefixInterpreter.Operand)
        1 new | M:$StageBPrefixInterpreter.FuelSignal.#ctor
        1 new | M:$StageBPrefixInterpreter.OutsideSignal.#ctor
        1 new | M:$StageBPrefixInterpreter.SuspendSignal.#ctor($StageBRefundOperand[])
        2 new | M:$StageBPrefixInterpreter.ValueLocation.#ctor(System.String,$StageBValue)
        5 new | M:$StageBPrefixRun.#ctor($StageBRunOutcomeKind,$StageBRefundSuspension,$StageBValue,System.String,$StageBRequest[],System.Int64,$StageBRejection)
        1 new | M:$StageBRefundOperand.#ctor(System.Int32,$StageBArgumentMode,$StageBValue,System.String,$StageBResolvedLocation)
        1 new | M:$StageBRefundSuspension.#ctor($StageBRefundOperand[],$StageBGasValue,System.Int64,$StageBValue,$StageBRequest[],System.Int64)
        1 new | M:$StageBSubtractBalanceRequest.#ctor(System.String,System.Numerics.BigInteger)
        1 new | M:$StageBTraceRequest.#ctor(System.String,$StageBValue[])
        2 set | M:$StageBPrefixInterpreter.Cell.set_Alias($StageBPrefixInterpreter.Location)
        """;

    private static void AdmitAdapters(CSharpCompilation compilation, INamedTypeSymbol kernel)
    {
        INamedTypeSymbol TypeNamed(string name) => compilation.GetTypeByMetadataName(Namespace + name) ?? throw new ExtractionException("Missing control adapter type: " + name);
        INamedTypeSymbol interpreter = TypeNamed("StageBPrefixInterpreter");
        SyntaxNode interpreterSyntax = Declaration(interpreter);
        SemanticModel interpreterModel = compilation.GetSemanticModel(interpreterSyntax.SyntaxTree);
        INamedTypeSymbol operationKind = TypeNamed("StageBOperationKind");
        if (operationKind.EnumUnderlyingType?.SpecialType != SpecialType.System_Int32 ||
            Tokens(Declaration(operationKind)) != ExpectedOperationKind)
            throw new ExtractionException("Control operation kind representation changed.");
        IMethodSymbol supportedPredicate = One(interpreter.GetMembers("IsSupportedOperation").OfType<IMethodSymbol>(), "Control supported operation predicate changed.");
        if (!supportedPredicate.IsStatic || supportedPredicate.DeclaredAccessibility != Accessibility.Private ||
            supportedPredicate.ReturnType.SpecialType != SpecialType.System_Boolean ||
            supportedPredicate.Parameters is not [{ Type: INamedTypeSymbol predicateKind }] ||
            !SymbolEqualityComparer.Default.Equals(predicateKind, operationKind) || supportedPredicate.GetAttributes().Length != 0 ||
            Declaration(supportedPredicate) is not MethodDeclarationSyntax { Body: null, ExpressionBody: not null } supportedDeclaration ||
            Tokens(supportedDeclaration) != Tokens(SyntaxFactory.ParseMemberDeclaration(SupportedOperationAdapter)!))
            throw new ExtractionException("Control supported operation predicate changed.");
        string[] expectedOperations =
        [
            "DeclarationExpression", "ExpressionStatement", "SimpleAssignment", "Invocation", "ObjectCreation",
            "CollectionExpression", "FieldReference", "PropertyReference", "LocalReference", "ParameterReference", "InstanceReference",
            "Literal", "DefaultValue", "Binary", "Unary", "Conversion", "Parenthesized", "Argument", "IsPattern", "ConstantPattern",
            "NegatedPattern", "IsNull", "FlowCapture", "FlowCaptureReference", "Discard",
        ];
        IFieldSymbol returnOperation = One(operationKind.GetMembers("Return").OfType<IFieldSymbol>(), "Control operation kind representation changed.");
        HashSet<object> admittedValues = [];
        MemberAccessExpressionSyntax[] supportedMembers = supportedDeclaration.ExpressionBody.Expression.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>().ToArray();
        if (supportedMembers.Length != expectedOperations.Length)
            throw new ExtractionException("Control supported operation predicate changed.");
        for (int index = 0; index < expectedOperations.Length; index++)
        {
            if (interpreterModel.GetSymbolInfo(supportedMembers[index]).Symbol is not IFieldSymbol member ||
                member.Name != expectedOperations[index] || !member.HasConstantValue ||
                !SymbolEqualityComparer.Default.Equals(member.ContainingType, operationKind) || member.ConstantValue is null ||
                Equals(member.ConstantValue, returnOperation.ConstantValue) || !admittedValues.Add(member.ConstantValue))
                throw new ExtractionException("Control supported operation predicate changed.");
        }
        IMethodSymbol validateSymbol = One(interpreter.GetMembers("Validate").OfType<IMethodSymbol>(), "Control Validate root changed.");
        if (!validateSymbol.IsStatic || validateSymbol.DeclaredAccessibility != Accessibility.Private || !validateSymbol.ReturnsVoid ||
            validateSymbol.Parameters is not [{ Type: INamedTypeSymbol validateProgram }] ||
            !SymbolEqualityComparer.Default.Equals(validateProgram, TypeNamed("StageBPrefixProgram")) ||
            Declaration(validateSymbol) is not MethodDeclarationSyntax { Body: not null } validate)
            throw new ExtractionException("Control Validate root changed.");
        LocalFunctionStatementSyntax validateNode = One(validate.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .Where(static local => local.Identifier.ValueText == "ValidateNode"), "Control validation node traversal changed.");
        if (Tokens(validateNode) != Tokens(SyntaxFactory.ParseStatement(ValidateNodeAdapter)))
            throw new ExtractionException("Control validation node traversal changed.");
        IMethodSymbol validateNodeSymbol = interpreterModel.GetDeclaredSymbol(validateNode) ?? throw new ExtractionException("Control validation node traversal changed.");
        ForEachStatementSyntax rootTraversal = One(validate.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Where(loop => loop.Identifier.ValueText == "node" && loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(call => SymbolEqualityComparer.Default.Equals(interpreterModel.GetSymbolInfo(call).Symbol, validateNodeSymbol))),
            "Control validation root traversal changed.");
        if (Tokens(rootTraversal) != Tokens(SyntaxFactory.ParseStatement(RootValidationAdapter)) ||
            rootTraversal.Parent?.Parent is not ForEachStatementSyntax { Identifier.ValueText: "block" } blockTraversal ||
            blockTraversal.Parent?.Parent is not ForEachStatementSyntax { Identifier.ValueText: "function" })
            throw new ExtractionException("Control validation root traversal changed.");
        InvocationExpressionSyntax[] validationCalls = validate.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => SymbolEqualityComparer.Default.Equals(interpreterModel.GetSymbolInfo(call).Symbol, validateNodeSymbol)).ToArray();
        if (validationCalls.Length != 2 || !validationCalls.Any(rootTraversal.DescendantNodes().Contains) ||
            !validationCalls.Any(validateNode.DescendantNodes().Contains))
            throw new ExtractionException("Control validation node call roster changed.");
        InvocationExpressionSyntax[] supportedCalls = validate.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => SymbolEqualityComparer.Default.Equals(interpreterModel.GetSymbolInfo(call).Symbol, supportedPredicate)).ToArray();
        if (supportedCalls.Length != 1 || !validateNode.DescendantNodes().Contains(supportedCalls[0]))
            throw new ExtractionException("Control validation supported predicate changed.");
        foreach (IdentifierNameSyntax identifier in validate.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            ISymbol? resolved = interpreterModel.GetSymbolInfo(identifier).Symbol;
            if (identifier.Identifier.ValueText == "IsSupportedOperation" && !SymbolEqualityComparer.Default.Equals(resolved, supportedPredicate))
                throw new ExtractionException("Control validation supported predicate rebound.");
            if (identifier.Identifier.ValueText is "StageBPrefixProgram" or "StageBPrefixIntegrity" or "StageBExecutionException" or
                    "StageBPrefixFunction" or "StageBPrefixBlock" or "StageBPrefixNode" or "StageBPrefixTargetKind" or "StageBRequestKind" &&
                !SymbolEqualityComparer.Default.Equals(resolved, TypeNamed(identifier.Identifier.ValueText)))
                throw new ExtractionException("Control validation type rebound: " + identifier.Identifier.ValueText);
        }
        string validateTokens = string.Concat(validate.DescendantTokens().Select(static token =>
            string.Create(CultureInfo.InvariantCulture, $"{token.RawKind}:{token.Text.Length}:{token.Text}")));
        string validateHash = CompilerReferences.Hash(Encoding.UTF8.GetBytes(validateTokens));
        if (validateHash != "3065661fcae79b9d39f37572a0fcdaabe0415e2fe0cbb3adc4d132d636f4d03a")
            throw new ExtractionException("Control Validate token shape changed: " + validateHash);
        INamedTypeSymbol frame = TypeNamed("StageBPrefixInterpreter+Frame");
        if (frame.DeclaringSyntaxReferences.Length != 1 || Declaration(frame) is not ClassDeclarationSyntax frameDeclaration ||
            Tokens(frameDeclaration) != "privatesealedclassFrame(StageBPrefixFunctionfunction,Operand@this){" +
                "internalStageBPrefixFunctionFunction{get;}=function;internalOperandThis{get;}=@this;" +
                "internalDictionary<string,Cell>Cells{get;}=new(StringComparer.Ordinal);internalDictionary<int,Operand>Captures{get;}=[];}" ||
            !SymbolEqualityComparer.Default.Equals(One(frame.GetMembers("Function").OfType<IPropertySymbol>(),
                "Control frame declaration changed.").Type, TypeNamed("StageBPrefixFunction")))
            throw new ExtractionException("Control frame declaration changed.");
        INamedTypeSymbol machine = TypeNamed("StageBPrefixInterpreter+Machine");
        if (machine.DeclaringSyntaxReferences.Length != 1 || machine.GetAttributes().Length != 0 ||
            Declaration(machine) is not ClassDeclarationSyntax machineDeclaration || machineDeclaration.AttributeLists.Count != 0 ||
            string.Concat(machineDeclaration.Modifiers.Select(static token => token.Text)) != "privatesealed" ||
            machineDeclaration.BaseList is not null || machineDeclaration.TypeParameterList is not null)
            throw new ExtractionException("Control Machine storage declaration changed.");
        RejectTypedReferenceIntrinsics(machineDeclaration);
        IFieldSymbol fuel = One(machine.GetMembers("_fuel").OfType<IFieldSymbol>(), "Control fuel field changed.");
        if (fuel.Type.SpecialType != SpecialType.System_Int64 || fuel.IsStatic || fuel.IsReadOnly || fuel.IsVolatile ||
            fuel.GetAttributes().Length != 0 || Declaration(fuel) is not VariableDeclaratorSyntax { Initializer: null } fuelVariable ||
            fuelVariable.Parent?.Parent is not FieldDeclarationSyntax fuelDeclaration || Tokens(fuelDeclaration) != "privatelong_fuel;")
            throw new ExtractionException("Control fuel field changed.");
        if (machineDeclaration.Members.OfType<FieldDeclarationSyntax>().Any(static field => field.AttributeLists.Count != 0) ||
            machine.GetMembers().OfType<IFieldSymbol>().Any(static field => field.GetAttributes().Length != 0))
            throw new ExtractionException("Control Machine field attributes are unsupported.");
        MethodDeclarationSyntax Method(string name) => Declaration(One(machine.GetMembers(name).OfType<IMethodSymbol>(), "Control adapter root changed.")) as MethodDeclarationSyntax
            ?? throw new ExtractionException("Control adapter declaration changed.");
        MethodDeclarationSyntax tick = Method("Tick");
        if (Tokens(tick.WithBody(null).WithSemicolonToken(default)) != "privatevoidTick()") throw new ExtractionException("Control Tick declaration changed.");
        if (Tokens(tick.Body!) != Tokens(SyntaxFactory.ParseStatement(TickAdapter))) throw new ExtractionException("Control Tick adapter changed.");
        MethodDeclarationSyntax execute = Method("Execute");
        if (execute.Body is null || Tokens(execute.WithBody(null).WithSemicolonToken(default)) != "privateStageBValueExecute(Frameframe)" ||
            Tokens(execute.Body) != Tokens(SyntaxFactory.ParseStatement(ExecuteAdapter))) throw new ExtractionException("Control edge adapter changed.");
        MethodDeclarationSyntax localCall = Method("Call");
        if (localCall.Body is null || Tokens(localCall.WithBody(null).WithSemicolonToken(default)) !=
            "privateOperandCall(Framecaller,StageBPrefixNodenode,StageBPrefixCallcall,Operand[]operands,boolconstructing)" ||
            Tokens(SyntaxFactory.Block(localCall.Body.Statements.TakeLast(4))) != Tokens(SyntaxFactory.ParseStatement(LocalReturnAdapter)))
            throw new ExtractionException("Control local return adapter changed.");
        SemanticModel localModel = compilation.GetSemanticModel(localCall.SyntaxTree);
        InvocationExpressionSyntax localSelection = One(localCall.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == "StageBControlKernel"),
            "Control local return call roster changed.");
        if (localModel.GetSymbolInfo(localSelection).Symbol is not IMethodSymbol selection ||
            !SymbolEqualityComparer.Default.Equals(selection.ContainingType, kernel) || selection.Name != "SelectLocalReturn")
            throw new ExtractionException("Control local return call rebound.");
        foreach (IdentifierNameSyntax identifier in localCall.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (identifier.Identifier.ValueText is "StageBLocalReturnKind" or "StageBPrefixOperandMode" or "StageBControlKernel" &&
                !SymbolEqualityComparer.Default.Equals(localModel.GetSymbolInfo(identifier).Symbol, TypeNamed(identifier.Identifier.ValueText)))
                throw new ExtractionException("Control local return type rebound.");
        MethodDeclarationSyntax transparent = Method("EvaluateTransparent");
        if (transparent.Body is null || Tokens(transparent.WithBody(null).WithSemicolonToken(default)) !=
            "privateOperandEvaluateTransparent(Frameframe,StageBPrefixNodenode)" ||
            Tokens(transparent.Body) != Tokens(SyntaxFactory.ParseStatement(TransparentAdapter)))
            throw new ExtractionException("Control transparent adapter changed.");
        SemanticModel transparentModel = compilation.GetSemanticModel(transparent.SyntaxTree);
        IMethodSymbol transparentSymbol = transparentModel.GetDeclaredSymbol(transparent) ?? throw new ExtractionException("Control transparent root changed.");
        IMethodSymbol transparentHelper = One(kernel.GetMembers("ShouldReadTransparent").OfType<IMethodSymbol>(), "Control transparent helper changed.");
        InvocationExpressionSyntax helperCall = One(transparent.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => SymbolEqualityComparer.Default.Equals(transparentModel.GetSymbolInfo(call).Symbol, transparentHelper)),
            "Control transparent helper call roster changed.");
        if (helperCall.ArgumentList.Arguments.Count != 2 ||
            !helperCall.ArgumentList.Arguments.Select(argument => transparentModel.GetOperation(argument) is IArgumentOperation operation
                ? operation.Parameter?.Ordinal : null).SequenceEqual(new int?[] { 0, 1 }))
            throw new ExtractionException("Control transparent helper arguments changed.");
        INamedTypeSymbol prefixNode = TypeNamed("StageBPrefixNode");
        foreach (MemberAccessExpressionSyntax access in transparent.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                     .Where(static access => access.Expression is IdentifierNameSyntax { Identifier.ValueText: "node" }))
            if (transparentModel.GetSymbolInfo(access).Symbol is not IPropertySymbol property ||
                !SymbolEqualityComparer.Default.Equals(property.ContainingType, prefixNode))
                throw new ExtractionException("Control transparent node projection rebound.");
        foreach (IdentifierNameSyntax identifier in transparent.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (identifier.Identifier.ValueText is "StageBOperationKind" or "StageBPrefixOperandMode" or "StageBControlKernel" or "StageBPrefixNode" &&
                !SymbolEqualityComparer.Default.Equals(transparentModel.GetSymbolInfo(identifier).Symbol, TypeNamed(identifier.Identifier.ValueText)))
                throw new ExtractionException("Control transparent type rebound: " + identifier.Identifier.ValueText);
        foreach (MethodDeclarationSyntax adapter in new[] { tick, execute })
        {
            SemanticModel model = compilation.GetSemanticModel(adapter.SyntaxTree);
            InvocationExpressionSyntax call = One(adapter.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == "StageBControlKernel"), "Control adapter call roster changed.");
            if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol target || !SymbolEqualityComparer.Default.Equals(target.ContainingType, kernel))
                throw new ExtractionException("Control adapter call rebound.");
            foreach (IdentifierNameSyntax identifier in adapter.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                string name = identifier.Identifier.ValueText;
                ISymbol? resolved = model.GetSymbolInfo(identifier).Symbol;
                if (name is "StageBEdgeKind" or "StageBTickResult" or "StageBControlKernel" &&
                    !SymbolEqualityComparer.Default.Equals(resolved, TypeNamed(name)))
                    throw new ExtractionException("Control adapter type rebound: " + name);
                if (name == "_fuel" && !SymbolEqualityComparer.Default.Equals(resolved, fuel))
                    throw new ExtractionException("Control adapter fuel reference rebound.");
                if (resolved is IPropertySymbol property && identifier.Parent is MemberAccessExpressionSyntax access &&
                    access.Expression.ToString() is "result" or "cursor" or "result.Cursor")
                {
                    string resultType = access.Expression.ToString() == "result" ? adapter == tick ? "StageBTickResult" : "StageBFinishResult`1" : "StageBBlockCursor`1";
                    if (!SymbolEqualityComparer.Default.Equals(property.ContainingType.OriginalDefinition, TypeNamed(resultType)))
                        throw new ExtractionException("Control result property rebound.");
                }
                if (identifier.Parent is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "StageBEdgeKind" } } &&
                    resolved is IFieldSymbol enumField && !SymbolEqualityComparer.Default.Equals(enumField.ContainingType, TypeNamed("StageBEdgeKind")))
                    throw new ExtractionException("Control result tag rebound.");
            }
            foreach (GenericNameSyntax generic in adapter.DescendantNodes().OfType<GenericNameSyntax>())
            {
                string name = generic.Identifier.ValueText;
                if (name is "StageBBlockCursor" or "StageBFinishResult" &&
                    (model.GetSymbolInfo(generic).Symbol is not INamedTypeSymbol type ||
                     !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, TypeNamed(name + "`1")) ||
                     type.TypeArguments.Length != 1 || !SymbolEqualityComparer.Default.Equals(type.TypeArguments[0], TypeNamed("StageBValue"))))
                    throw new ExtractionException("Control adapter type rebound: " + name);
            }
        }
        foreach ((string name, string type) in new[] { ("Kind", Namespace + "StageBValueKind"), ("Boolean", "bool"), ("Type", "string") })
        {
            IPropertySymbol property = One(TypeNamed("StageBValue").GetMembers(name).OfType<IPropertySymbol>(), "Control value projection changed.");
            if (Declaration(property) is not PropertyDeclarationSyntax syntax) throw new ExtractionException("Control value declaration changed.");
            if (property.Type.ToDisplayString() != type || property.IsStatic || property.GetAttributes().Length != 0 ||
                syntax.ExpressionBody is not null || syntax.Initializer is not null || syntax.AccessorList?.Accessors is not { Count: 1 } accessors ||
                accessors[0].Kind() != SyntaxKind.GetAccessorDeclaration || accessors[0].Body is not null || accessors[0].ExpressionBody is not null)
                throw new ExtractionException("Control value projection is not an auto getter.");
        }
        AdmitRuntimeRepresentations(compilation);
        RequireRecord("StageBPrefixBlock", "internalsealedrecordStageBPrefixBlock(intOrdinal,StageBPrefixNode[]Operations,StageBPrefixNode?BranchValue,stringConditionKind,StageBPrefixExitExit,StageBEdge?FallThrough,StageBEdge?Conditional);");
        RequireRecord("StageBEdge", "internalsealedrecordStageBEdge(intDestination,stringSemantics,int[]LeavingRegions,int[]EnteringRegions,int[]FinallyRegions);");
        RequireRecord("StageBCfgPoint", "internalsealedrecordStageBCfgPoint(intBlock,intOperation);");
        RequireRecord("StageBPrefixNode", "internalsealedrecordStageBPrefixNode(StageBOperationKindKind,stringType,stringSymbol,stringOperator,stringConstant,stringConversion,boolImplicit,StageBPrefixOperandModeMode,StageBTermBindingBinding,StageBPrefixCall?Call,StageBPrefixTarget[]AdditionalTargets,StageBPrefixNode[]Children,boolCompletes);");
        RequireRecord("StageBPrefixCall", "internalsealedrecordStageBPrefixCall(StageBPrefixTargetTarget,intReceiverChild,StageBPrefixArgument[]Arguments);");
        RequireRecord("StageBPrefixTarget", "internalsealedrecordStageBPrefixTarget(StageBPrefixTargetKindKind,StageBMemberMember,stringBody,StageBRequestKind?RequestKind);");
        RequireRecord("StageBPrefixArgument", "internalsealedrecordStageBPrefixArgument(intChild,intOrdinal,StageBArgumentModeMode,boolImplicit,stringKind);");
        RequireRecord("StageBMember", "internalsealedrecordStageBMember(stringSymbol,stringDefinition,stringName,stringDeclaringType,StageBMemberKindKind,StageBReceiverKindReceiver,stringType,StageBRefKindRefKind,StageBParameter[]Parameters);");
        RequireRecord("StageBParameter", "internalsealedrecordStageBParameter(stringSymbol,stringName,intOrdinal,stringType,StageBRefKindRefKind,boolOptional);");
        RequireRecord("StageBPrefixFunction", "internalsealedrecordStageBPrefixFunction(StageBMemberSignature,StageBCfgPointEntry,StageBBinding[]Bindings,StageBCapture[]Captures,StageBRegion[]Regions,string[]EntryFacts,StageBPrefixBlock[]Blocks,string[]Calls,StageBPrefixCaptureMode[]CaptureModes,StageBPrefixBlockBound[]BlockBounds,longFuelBound,boolMayReturn);");
        RequireRecord("StageBPrefixProgram", "internalsealedrecordStageBPrefixProgram(stringEntry,stringPolicyType,StageBPrefixFunction[]Functions,string[]Initializers,StageBMember[]Members,StageBType[]Types,StageBPrefixSuspensionRefund,string[]CalleeBeforeCaller,longFuelBound,stringScope,SourceIdentity[]CompilerSources,ReferenceIdentity[]CompilerReferences,string[]ExternalPremises,StageBStage[]AcceptedStages,StageBInertFunctionPostRefundFeeHelper,stringIntegrity);");
        RequireEnum("StageBPrefixExit", "internalenumStageBPrefixExit{Branch,Return,Suspend,OutsideSelectedDomain}");
        RequireEnum("StageBValueKind", "internalenumStageBValueKind{Unit,Null,Bool,UInt64,Int64,UInt256,Enum,Address,Bytes,Reference,Struct}");
        RequireEnum("StageBPrefixOperandMode", "internalenumStageBPrefixOperandMode{Value,Location,ReadOnlyLocation}");
        RequireEnum("StageBPrefixTargetKind", "internalenumStageBPrefixTargetKind{Local,StaticField,DefaultValue,Initialization,StateCharge,ExternalRequest,RefundSuspension}");
        RequireEnum("StageBRefKind", "internalenumStageBRefKind{None,Ref,Out,In,RefReadOnly,RefReadOnlyParameter}");
        RequireEnum("StageBArgumentMode", "internalenumStageBArgumentMode{Value,ReadOnlyLocation,ReadOnlyTemporary,WritableLocation,OutLocation}");
        RequireEnum("StageBMemberKind", "internalenumStageBMemberKind{Method,Constructor,Property,Field}");
        RequireEnum("StageBReceiverKind", "internalenumStageBReceiverKind{Static,Reference,Value}");
        void RequireEnum(string name, string expected)
        {
            if (Tokens(Declaration(TypeNamed(name))) != expected)
                throw new ExtractionException("Control projection enum changed: " + name);
        }
        void RequireRecord(string name, string expected)
        {
            if (Declaration(TypeNamed(name)) is not RecordDeclarationSyntax record || Tokens(record) != expected)
                throw new ExtractionException("Control projection record changed: " + name);
            SemanticModel model = compilation.GetSemanticModel(record.SyntaxTree);
            foreach (IdentifierNameSyntax identifier in record.ParameterList!.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, TypeNamed(identifier.Identifier.ValueText)))
                    throw new ExtractionException("Control projection type rebound.");
        }
        foreach (MethodDeclarationSyntax adapter in new[] { tick, execute })
        {
            SemanticModel model = compilation.GetSemanticModel(adapter.SyntaxTree);
            foreach (IdentifierNameSyntax identifier in adapter.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (identifier.Identifier.ValueText is "StageBValue" or "StageBValueKind" or "StageBPrefixExit" or "StageBPrefixBlock" &&
                    !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier).Symbol, TypeNamed(identifier.Identifier.ValueText)))
                    throw new ExtractionException("Control projection type rebound.");
        }
        MethodDeclarationSyntax evaluate = Method("Evaluate");
        if (evaluate.Body is not { Statements.Count: > 0 } || Tokens(evaluate.Body.Statements[0]) != "Tick();")
            throw new ExtractionException("Evaluation bypasses the control tick.");
        IMethodSymbol tickSymbol = One(machine.GetMembers("Tick").OfType<IMethodSymbol>(), "Control Tick root changed.");
        SemanticModel evaluateModel = compilation.GetSemanticModel(evaluate.SyntaxTree);
        InvocationExpressionSyntax tickCall = One(evaluate.Body.Statements[0].DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            "Control evaluation tick call changed.");
        if (!SymbolEqualityComparer.Default.Equals(evaluateModel.GetSymbolInfo(tickCall).Symbol, tickSymbol))
            throw new ExtractionException("Control evaluation tick call rebound.");
        SwitchExpressionArmSyntax transparentDispatch = One(evaluate.DescendantNodes().OfType<SwitchExpressionArmSyntax>()
            .Where(arm => arm.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(call => SymbolEqualityComparer.Default.Equals(evaluateModel.GetSymbolInfo(call).Symbol, transparentSymbol))),
            "Control transparent dispatch changed.");
        if (Tokens(transparentDispatch) != TransparentDispatchAdapter)
            throw new ExtractionException("Control transparent dispatch changed.");
        SyntaxNode machineSyntax = Declaration(machine);
        SemanticModel machineModel = compilation.GetSemanticModel(machineSyntax.SyntaxTree);
        string sourceCalls = SourceCallRoster(machineModel, machineSyntax);
        if (sourceCalls != ExpectedMachineSourceCalls)
            throw new ExtractionException("Control Machine call roster changed.");
        foreach (SyntaxNode node in machineSyntax.DescendantNodes())
        {
            if (node is TypeOfExpressionSyntax or UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax ||
                node is IdentifierNameSyntax { Identifier.ValueText: "dynamic" } && machineModel.GetTypeInfo(node).Type?.TypeKind == TypeKind.Dynamic)
                throw new ExtractionException("Control indirect fuel access is unsupported.");
            if (node is ThisExpressionSyntax && SymbolEqualityComparer.Default.Equals(machineModel.GetTypeInfo(node).Type, machine))
                throw new ExtractionException("Control explicit Machine instance escape is unsupported.");
            if (node is InvocationExpressionSyntax invocation)
            {
                if (machineModel.GetOperation(invocation) is IDynamicInvocationOperation)
                    throw new ExtractionException("Control indirect fuel access is unsupported.");
                if (machineModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target) continue;
                string type = target.ContainingType.ToDisplayString();
                if (target.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal) ||
                    type is "System.Type" or "System.Runtime.CompilerServices.Unsafe" or "System.Runtime.InteropServices.Marshal" ||
                    target.Name == "GetType" && type == "object" || target.GetAttributes().Any(static attribute =>
                        attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.UnsafeAccessorAttribute"))
                    throw new ExtractionException("Control indirect fuel access is unsupported.");
            }
        }
        InvocationExpressionSyntax[] callers = machineSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => SymbolEqualityComparer.Default.Equals(machineModel.GetSymbolInfo(call).Symbol, tickSymbol)).ToArray();
        if (callers.Length != 2 || !callers.Any(call => call.Ancestors().Contains(execute)) || !callers.Any(call => call.Ancestors().Contains(evaluate)))
            throw new ExtractionException("Control tick call-site roster changed.");
        ConstructorDeclarationSyntax constructor = One(machineSyntax.DescendantNodes().OfType<ConstructorDeclarationSyntax>(), "Control machine constructor changed.");
        if (constructor.Body is null || Tokens(constructor.WithBody(null).WithSemicolonToken(default)) !=
            "internalMachine(StageBPrefixProgramprogram,StageBPostNonceInputinput,StageBResponseTaperesponses,longfuel)" ||
            Tokens(constructor.Body) != Tokens(SyntaxFactory.ParseStatement(ConstructorAdapter)))
            throw new ExtractionException("Control initial fuel adapter changed.");
        int writes = 0;
        foreach (IdentifierNameSyntax identifier in interpreterSyntax.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(interpreterModel.GetSymbolInfo(identifier).Symbol, fuel)) continue;
            if (identifier.Ancestors().Any(static node => node is AnonymousFunctionExpressionSyntax))
                throw new ExtractionException("Control captured fuel alias is unsupported.");
            if (identifier.Ancestors().Any(static node => node is RefExpressionSyntax or ArgumentSyntax { RefKindKeyword.RawKind: not 0 }))
                throw new ExtractionException("Control fuel alias is unsupported.");
            SyntaxNode target = identifier.Parent is MemberAccessExpressionSyntax member ? member : identifier;
            IOperation? reference = interpreterModel.GetOperation(target);
            for (IOperation? ancestor = reference?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is IIncrementOrDecrementOperation mutation && mutation.Target.Syntax.Span.Contains(identifier.Span))
                    throw new ExtractionException("Control fuel mutation bypass.");
                if (ancestor is IAssignmentOperation assignment && assignment.Target.Syntax.Span.Contains(identifier.Span))
                {
                    writes++;
                    if (assignment is not ISimpleAssignmentOperation ||
                        !assignment.Syntax.Ancestors().Contains(tick) && !assignment.Syntax.Ancestors().Contains(constructor))
                        throw new ExtractionException("Control fuel write bypass.");
                }
            }
        }
        if (writes != 2) throw new ExtractionException("Control fuel write roster changed.");
        if (Declaration(One(interpreter.GetMembers("Run").OfType<IMethodSymbol>(), "Control Run root changed.")) is not MethodDeclarationSyntax run)
            throw new ExtractionException("Control Run declaration changed.");
        if (run.Body is null || Tokens(run.WithBody(null).WithSemicolonToken(default)) !=
            "internalstaticStageBPrefixRunRun(StageBPrefixProgramprogram,StageBPostNonceInputinput,StageBResponseTaperesponses,longfuel)" ||
            Tokens(run.Body) != Tokens(SyntaxFactory.ParseStatement(RunAdapter))) throw new ExtractionException("Control initial fuel guard changed.");
        SemanticModel runModel = compilation.GetSemanticModel(run.SyntaxTree);
        foreach (MemberAccessExpressionSyntax access in run.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(static access => access.ToString() == "program.FuelBound"))
            if (runModel.GetSymbolInfo(access).Symbol is not IPropertySymbol property || property.Type.SpecialType != SpecialType.System_Int64 ||
                !SymbolEqualityComparer.Default.Equals(property.ContainingType, TypeNamed("StageBPrefixProgram")))
                throw new ExtractionException("Control initial fuel bound rebound.");
        ObjectCreationExpressionSyntax machineCreation = One(run.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(static creation => creation.Type.ToString() == "Machine"), "Control machine call changed.");
        if (runModel.GetSymbolInfo(machineCreation).Symbol is not IMethodSymbol constructed || !SymbolEqualityComparer.Default.Equals(constructed.ContainingType, machine))
            throw new ExtractionException("Control initial machine constructor rebound.");
        string machineTokens = string.Concat(machineSyntax.DescendantTokens().Select(static token =>
            string.Create(CultureInfo.InvariantCulture, $"{token.RawKind}:{token.Text.Length}:{token.Text}")));
        string machineHash = CompilerReferences.Hash(Encoding.UTF8.GetBytes(machineTokens));
        if (machineHash != "626bae6419899519cfbd372ece9b8136e7e72ce877110d7d651ba6dc28b4d789")
            throw new ExtractionException("Control Machine token shape changed: " + machineHash);
    }

    private const string ConstructorAdapter = """
        {
            _program = program;
            _input = input;
            _responses = responses;
            _fuel = fuel;
            _functions = program.Functions.ToDictionary(static function => function.Signature.Symbol, StringComparer.Ordinal);
        }
        """;

    private const string LocalReturnAdapter = """
        {
            StageBValue result = Execute(frame);
            StageBLocalReturnKind returnKind = StageBControlKernel.SelectLocalReturn(constructing, node.Mode == StageBPrefixOperandMode.Value);
            if (returnKind == StageBLocalReturnKind.ReturnedValue) return Operand.Value(result);
            return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : receiver;
        }
        """;

    private const string TransparentAdapter = """
        {
            if (node.Children.Length != 1) throw new StageBExecutionException("transparent-arity", node.Kind.ToString());
            Operand operand = Evaluate(frame, node.Children[0]);
            if (node.Kind == StageBOperationKind.Conversion && node.Call is { } call)
                return Call(frame, node, call, [operand], constructing: false);
            if (node.Kind == StageBOperationKind.Conversion && node.Mode == StageBPrefixOperandMode.Value)
                return Operand.Value(ConvertValue(operand.Read(), node.Type));
            bool preservesOperand = node.Kind is StageBOperationKind.Argument or StageBOperationKind.DeclarationExpression;
            return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)
                ? Operand.Value(operand.Read()) : operand;
        }
        """;

    private const string TransparentDispatchAdapter =
        "StageBOperationKind.ExpressionStatementorStageBOperationKind.ParenthesizedorStageBOperationKind.Argumentor" +
        "StageBOperationKind.DeclarationExpressionorStageBOperationKind.Conversion=>EvaluateTransparent(frame,node)";

    private const string RootValidationAdapter = """
        foreach (StageBPrefixNode node in block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue])
            ValidateNode(node);
        """;

    private const string SupportedOperationAdapter = """
        private static bool IsSupportedOperation(StageBOperationKind kind) => kind is
            StageBOperationKind.DeclarationExpression or StageBOperationKind.ExpressionStatement or StageBOperationKind.SimpleAssignment or
            StageBOperationKind.Invocation or StageBOperationKind.ObjectCreation or StageBOperationKind.CollectionExpression or
            StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference or StageBOperationKind.LocalReference or
            StageBOperationKind.ParameterReference or StageBOperationKind.InstanceReference or StageBOperationKind.Literal or
            StageBOperationKind.DefaultValue or StageBOperationKind.Binary or StageBOperationKind.Unary or StageBOperationKind.Conversion or
            StageBOperationKind.Parenthesized or StageBOperationKind.Argument or StageBOperationKind.IsPattern or
            StageBOperationKind.ConstantPattern or StageBOperationKind.NegatedPattern or StageBOperationKind.IsNull or
            StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or StageBOperationKind.Discard;
        """;

    private const string ExpectedOperationKind =
        "internalenumStageBOperationKind{OutsideSelectedDomain,Block,VariableDeclarationGroup,VariableDeclaration,VariableDeclarator," +
        "VariableInitializer,DeclarationExpression,ExpressionStatement,Return,Conditional,ConditionalAccess,ConditionalAccessInstance," +
        "Coalesce,SimpleAssignment,CompoundAssignment,Invocation,ObjectCreation,ObjectOrCollectionInitializer,MemberInitializer," +
        "CollectionExpression,ArrayCreation,ArrayInitializer,FieldReference,PropertyReference,LocalReference,ParameterReference," +
        "InstanceReference,Literal,DefaultValue,Binary,Unary,Conversion,Parenthesized,Argument,IsPattern,ConstantPattern,NegatedPattern," +
        "DeclarationPattern,DiscardPattern,SwitchExpression,SwitchExpressionArm,Discard,NameOf,IsNull,UsingDeclaration,FlowCapture," +
        "FlowCaptureReference,Loop,Branch,Tuple,DeconstructionAssignment,Throw,}";

    private const string ValidateNodeAdapter = """
        static void ValidateNode(StageBPrefixNode node)
        {
            if (!IsSupportedOperation(node.Kind)) throw new StageBExecutionException("unsupported-operation", node.Kind.ToString());
            if (node.AdditionalTargets.Any(static target => target is not
                { Kind: StageBPrefixTargetKind.ExternalRequest, RequestKind: StageBRequestKind.Collection, Member.Name: "Add" }))
                throw new StageBExecutionException("unsupported-additional-target", node.Kind.ToString());
            foreach (StageBPrefixNode child in node.Children) ValidateNode(child);
        }
        """;

    private const string RunAdapter = """
        {
            try
            {
                Validate(program);
                input.Validate();
                if (fuel <= 0 || fuel > program.FuelBound) throw new StageBExecutionException("fuel", $"expected 1..{program.FuelBound}");
                return new Machine(program, input, responses, fuel).Run();
            }
            catch (FuelSignal)
            {
                return new(StageBRunOutcomeKind.FuelExhausted, null, null, "fuel exhausted", []);
            }
            catch (StageBExecutionException exception)
            {
                return new(StageBRunOutcomeKind.Rejected, null, null, exception.Message, [], fuel, exception.Rejection);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return new(StageBRunOutcomeKind.Fault, null, null, exception.Message, []);
            }
        }
        """;

    private const string TickAdapter = """
        {
            StageBTickResult result = StageBControlKernel.Tick(_fuel);
            if (result.Exhausted) throw new FuelSignal();
            _fuel = result.RemainingFuel;
        }
        """;

    private const string ExecuteAdapter = """
        {
            Dictionary<int, StageBPrefixBlock> blocks = frame.Function.Blocks.ToDictionary(static block => block.Ordinal);
            StageBBlockCursor<StageBValue> cursor = new(frame.Function.Entry.Block, StageBValue.Unit);
            while (true)
            {
                Tick();
                if (!blocks.TryGetValue(cursor.Ordinal, out StageBPrefixBlock? block)) throw new StageBExecutionException("cfg-block", cursor.Ordinal.ToString(CultureInfo.InvariantCulture));
                if (block.Exit == StageBPrefixExit.OutsideSelectedDomain) throw new OutsideSignal();
                StageBValue last = cursor.Carried;
                foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();
                if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();
                StageBFinishResult<StageBValue> result = StageBControlKernel.FinishBlock(block.Exit, new StageBBlockCursor<StageBValue>(cursor.Ordinal, last),
                    block.ConditionKind, last.Kind == StageBValueKind.Bool, last.Boolean,
                    block.FallThrough is not null, block.FallThrough?.Destination ?? -1, block.FallThrough?.Semantics == "Return",
                    block.Conditional is not null, block.Conditional?.Destination ?? -1, block.Conditional?.Semantics == "Return");
                if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;
                if (result.Kind == StageBEdgeKind.MissingSuspension) throw new StageBExecutionException("missing-suspension", frame.Function.Signature.Name);
                if (result.Kind == StageBEdgeKind.InvalidCondition)
                    throw new StageBExecutionException("cfg-condition", $"{frame.Function.Signature.Name}:{cursor.Ordinal}:Stage-B execution bool: {last.Type}.");
                if (result.Kind != StageBEdgeKind.Jump) throw new StageBExecutionException("cfg-edge", frame.Function.Signature.Name);
                cursor = result.Cursor;
            }
        }
        """;
}
