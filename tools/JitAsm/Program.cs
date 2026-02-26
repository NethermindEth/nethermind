// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Reflection;
using System.Runtime.CompilerServices;
using Spectre.Console;

namespace JitAsm;

internal static class Program
{
    private static readonly Option<FileInfo> AssemblyOption = new("-a", "--assembly")
    {
        Description = "Path to the assembly containing the method",
        Required = true
    };

    private static readonly Option<string?> TypeOption = new("-t", "--type")
    {
        Description = "Fully qualified type name (optional, will search all types if not specified)"
    };

    private static readonly Option<string> MethodOption = new("-m", "--method")
    {
        Description = "Method name to disassemble",
        Required = true
    };

    private static readonly Option<string?> TypeParamsOption = new("--type-params")
    {
        Description = "Method generic type parameters (comma-separated type names)"
    };

    private static readonly Option<string?> ClassTypeParamsOption = new("--class-type-params")
    {
        Description = "Class generic type parameters (comma-separated type names, e.g., for TransactionProcessorBase`1)"
    };

    private static readonly Option<bool> SkipCctorOption = new("--skip-cctor-detection")
    {
        Description = "Skip automatic static constructor detection (single pass only)"
    };

    private static readonly Option<bool> VerboseOption = new("-v", "--verbose")
    {
        Description = "Show resolution details and both passes"
    };

    private static int Main(string[] args)
    {
        // Check if running in internal runner mode
        if (args.Length > 0 && args[0] == "--internal-runner")
        {
            return RunInternalRunner(args.Skip(1).ToArray());
        }

        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        var rootCommand = new RootCommand("JIT Assembly Disassembler - Generate JIT assembly output for .NET methods")
        {
            AssemblyOption,
            TypeOption,
            MethodOption,
            TypeParamsOption,
            ClassTypeParamsOption,
            SkipCctorOption,
            VerboseOption
        };

        rootCommand.SetAction(parseResult =>
        {
            var assembly = parseResult.GetValue(AssemblyOption)!;
            var typeName = parseResult.GetValue(TypeOption);
            var methodName = parseResult.GetValue(MethodOption)!;
            var typeParams = parseResult.GetValue(TypeParamsOption);
            var classTypeParams = parseResult.GetValue(ClassTypeParamsOption);
            var skipCctor = parseResult.GetValue(SkipCctorOption);
            var verbose = parseResult.GetValue(VerboseOption);

            Execute(assembly, typeName, methodName, typeParams, classTypeParams, skipCctor, verbose);
        });

        return rootCommand.Parse(args).Invoke();
    }

    private static void Execute(FileInfo assembly, string? typeName, string methodName, string? typeParams, string? classTypeParams, bool skipCctor, bool verbose)
    {
        if (!assembly.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Assembly not found: {assembly.FullName}");
            return;
        }

        if (verbose)
        {
            AnsiConsole.MarkupLine($"[blue]Assembly:[/] {assembly.FullName}");
            AnsiConsole.MarkupLine($"[blue]Type:[/] {typeName ?? "(search all)"}");
            AnsiConsole.MarkupLine($"[blue]Method:[/] {methodName}");
            if (classTypeParams is not null)
            {
                AnsiConsole.MarkupLine($"[blue]Class Type Parameters:[/] {classTypeParams}");
            }
            if (typeParams is not null)
            {
                AnsiConsole.MarkupLine($"[blue]Method Type Parameters:[/] {typeParams}");
            }
            AnsiConsole.WriteLine();
        }

        var runner = new JitRunner(assembly.FullName, typeName, methodName, typeParams, classTypeParams, verbose);

        JitResult result = (skipCctor ?
            runner.RunSinglePassAsync() :
            runner.RunTwoPassAsync())
            .GetAwaiter().GetResult();

        OutputResult(result, verbose);
    }

    private static void OutputResult(JitResult result, bool verbose)
    {
        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
            if (!string.IsNullOrEmpty(result.Output))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Output:[/]");
                AnsiConsole.WriteLine(result.Output);
            }
            return;
        }

        if (verbose && result.DetectedCctors.Count > 0)
        {
            AnsiConsole.MarkupLine("[blue]Detected static constructors:[/]");
            foreach (var cctor in result.DetectedCctors)
            {
                AnsiConsole.MarkupLine($"  [grey]- {Markup.Escape(cctor)}[/]");
            }
            AnsiConsole.WriteLine();
        }

        if (verbose && result.Pass1Output is not null && result.DetectedCctors.Count > 0)
        {
            AnsiConsole.MarkupLine("[blue]Pass 1 Output (before cctor initialization):[/]");
            AnsiConsole.WriteLine(result.Pass1Output);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Pass 2 Output (after cctor initialization):[/]");
        }

        AnsiConsole.WriteLine(result.Output ?? string.Empty);
    }

    private static int RunInternalRunner(string[] args)
    {
        // Parse internal runner arguments
        // Format: <assembly> <method> [--type <type>] [--type-params <params>] [--class-type-params <params>] [--init-cctors <types>]
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Internal runner requires assembly and method arguments");
            return 1;
        }

        var assemblyPath = args[0];
        var methodName = args[1];
        string? typeName = null;
        string? typeParams = null;
        string? classTypeParams = null;
        List<string> cctorsToInit = [];

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type" when i + 1 < args.Length:
                    typeName = args[++i];
                    break;
                case "--type-params" when i + 1 < args.Length:
                    typeParams = args[++i];
                    break;
                case "--class-type-params" when i + 1 < args.Length:
                    classTypeParams = args[++i];
                    break;
                case "--init-cctors" when i + 1 < args.Length:
                    cctorsToInit = args[++i].Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                    break;
            }
        }

        try
        {
            // Load the assembly
            var assembly = Assembly.LoadFrom(assemblyPath);

            // Debug: Check if verbose environment variable is set
            bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";
            if (verbose)
            {
                Console.Error.WriteLine($"[DEBUG] Loaded assembly: {assembly.FullName}");
                Console.Error.WriteLine($"[DEBUG] Type name: {typeName}");
                Console.Error.WriteLine($"[DEBUG] Method name: {methodName}");
                Console.Error.WriteLine($"[DEBUG] Type params: {typeParams}");
                Console.Error.WriteLine($"[DEBUG] Class type params: {classTypeParams}");
            }

            // Initialize static constructors if requested
            foreach (var cctorTypeName in cctorsToInit)
            {
                var cctorType = ResolveType(assembly, cctorTypeName);
                if (cctorType is not null)
                {
                    RuntimeHelpers.RunClassConstructor(cctorType.TypeHandle);
                }
            }

            // Resolve the method
            var resolver = new MethodResolver(assembly);
            var method = resolver.ResolveMethod(typeName, methodName, typeParams, classTypeParams);

            if (method is null)
            {
                if (verbose)
                {
                    // Try to provide more info about what was found
                    var types = assembly.GetTypes().Where(t => t.Name.Contains(typeName ?? "")).Take(5);
                    Console.Error.WriteLine($"[DEBUG] Possible types: {string.Join(", ", types.Select(t => t.FullName))}");
                }
                Console.Error.WriteLine($"Could not resolve method: {methodName}");
                return 1;
            }

            // Prepare the method to trigger JIT compilation
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static Type? ResolveType(Assembly assembly, string typeName)
    {
        // Try direct lookup first
        var type = assembly.GetType(typeName);
        if (type is not null) return type;

        // Try searching all types
        foreach (var t in assembly.GetTypes())
        {
            if (t.FullName == typeName || t.Name == typeName)
            {
                return t;
            }
        }

        // Try loading from other assemblies
        return Type.GetType(typeName);
    }
}
