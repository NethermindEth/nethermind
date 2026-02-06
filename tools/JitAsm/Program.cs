// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
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

    private static readonly Option<bool> FullOptsOption = new("--fullopts")
    {
        Description = "Use single-pass FullOpts compilation (DOTNET_TieredCompilation=0) instead of the default Tier-1 + PGO simulation"
    };

    private static readonly Option<bool> NoAnnotateOption = new("--no-annotate")
    {
        Description = "Disable per-instruction annotations (throughput, latency, uops, ports from uops.info)"
    };

    private static readonly Option<string> ArchOption = new("--arch")
    {
        Description = "Target microarchitecture for annotations (default: zen4). Options: zen4, zen3, zen2, alder-lake, rocket-lake, ice-lake, tiger-lake, skylake",
        DefaultValueFactory = _ => "zen4"
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
            FullOptsOption,
            NoAnnotateOption,
            ArchOption,
            VerboseOption
        };

        int exitCode = 0;
        rootCommand.SetAction(parseResult =>
        {
            var assembly = parseResult.GetValue(AssemblyOption)!;
            var typeName = parseResult.GetValue(TypeOption);
            var methodName = parseResult.GetValue(MethodOption)!;
            var typeParams = parseResult.GetValue(TypeParamsOption);
            var classTypeParams = parseResult.GetValue(ClassTypeParamsOption);
            var skipCctor = parseResult.GetValue(SkipCctorOption);
            var fullOpts = parseResult.GetValue(FullOptsOption);
            var annotate = !parseResult.GetValue(NoAnnotateOption);
            var arch = parseResult.GetValue(ArchOption)!;
            var verbose = parseResult.GetValue(VerboseOption);

            exitCode = Execute(assembly, typeName, methodName, typeParams, classTypeParams, skipCctor, fullOpts, annotate, arch, verbose);
        });

        int parseExitCode = rootCommand.Parse(args).Invoke();
        return parseExitCode != 0 ? parseExitCode : exitCode;
    }

    private static int Execute(FileInfo assembly, string? typeName, string methodName, string? typeParams, string? classTypeParams, bool skipCctor, bool fullOpts, bool annotate, string arch, bool verbose)
    {
        if (!assembly.Exists)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Assembly not found: {assembly.FullName}");
            return 1;
        }

        bool tier1 = !fullOpts;

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
            AnsiConsole.MarkupLine(tier1
                ? "[blue]Mode:[/] Tier-1 + Dynamic PGO (default)"
                : "[blue]Mode:[/] FullOpts (TieredCompilation=0)");
            AnsiConsole.WriteLine();
        }

        var runner = new JitRunner(assembly.FullName, typeName, methodName, typeParams, classTypeParams, verbose, tier1);

        InstructionDb? instructionDb = annotate ? LoadOrBuildInstructionDb(arch, verbose) : null;

        JitResult result = (skipCctor ?
            runner.RunSinglePassAsync() :
            runner.RunTwoPassAsync())
            .GetAwaiter().GetResult();

        return OutputResult(result, verbose, instructionDb);
    }

    private static int OutputResult(JitResult result, bool verbose, InstructionDb? instructionDb = null)
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
            return 1;
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

        string output = result.Output ?? string.Empty;
        if (instructionDb is not null)
        {
            output = InstructionAnnotator.Annotate(output, instructionDb);
        }

        Console.WriteLine(output);
        return 0;
    }

    private static InstructionDb? LoadOrBuildInstructionDb(string arch, bool verbose)
    {
        string toolDir = AppContext.BaseDirectory;
        // Look for files relative to the project source directory first, then the binary directory
        string projectDir = Path.GetFullPath(Path.Combine(toolDir, "..", "..", "..", ".."));
        if (!File.Exists(Path.Combine(projectDir, "JitAsm.csproj")))
        {
            // Fallback: try to find the project directory from the current working directory
            string cwd = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(cwd, "tools", "JitAsm", "JitAsm.csproj")))
                projectDir = Path.Combine(cwd, "tools", "JitAsm");
            else if (File.Exists(Path.Combine(cwd, "JitAsm.csproj")))
                projectDir = cwd;
            else
                projectDir = toolDir;
        }

        string dbPath = Path.Combine(projectDir, "instructions.db");
        string xmlPath = Path.Combine(projectDir, "instructions.xml");

        // Check if we have a cached .db for this architecture
        if (File.Exists(dbPath))
        {
            try
            {
                var db = InstructionDb.Load(dbPath);
                string targetArch = InstructionDbBuilder.ResolveArchName(arch);
                if (db.ArchName == targetArch)
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"[blue]Loaded instruction database:[/] {dbPath} ({db.Count} entries for {db.ArchName})");
                    return db;
                }

                if (verbose)
                    AnsiConsole.MarkupLine($"[yellow]Cached DB is for {db.ArchName}, need {targetArch}. Rebuilding...[/]");
            }
            catch (Exception ex)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"[yellow]Failed to load cached DB: {Markup.Escape(ex.Message)}. Rebuilding...[/]");
            }
        }

        // Build from XML
        if (!File.Exists(xmlPath))
        {
            AnsiConsole.MarkupLine("[yellow]Instruction database not found. Annotations disabled.[/]");
            AnsiConsole.MarkupLine("[yellow]Download it with:[/]");
            AnsiConsole.MarkupLine($"  curl -o {Markup.Escape(xmlPath)} https://uops.info/instructions.xml");
            AnsiConsole.MarkupLine("[yellow]Or use --no-annotate to suppress this warning.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[blue]Building instruction database for {arch}...[/]");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var builtDb = InstructionDbBuilder.Build(xmlPath, arch);
        stopwatch.Stop();

        AnsiConsole.MarkupLine($"[blue]Built {builtDb.Count} instruction entries in {stopwatch.Elapsed.TotalSeconds:F1}s[/]");

        try
        {
            builtDb.Save(dbPath);
            if (verbose)
                AnsiConsole.MarkupLine($"[blue]Saved to:[/] {dbPath}");
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not save DB cache: {Markup.Escape(ex.Message)}[/]");
        }

        return builtDb;
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
        bool tier1 = false;

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
                case "--tier1":
                    tier1 = true;
                    break;
            }
        }

        // Set up dependency resolution for the target assembly.
        // Without this, RuntimeHelpers.PrepareMethod silently fails when the
        // JIT can't resolve types from transitive NuGet packages (e.g. Nethermind.Int256).
        bool verbose = Environment.GetEnvironmentVariable("JITASM_VERBOSE") == "1";

        // Build assembly name → file path mapping from the deps.json file.
        // This resolves both project references (in the same directory) and
        // NuGet packages (from the global packages cache).
        var assemblyDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        var assemblyMap = BuildAssemblyMap(assemblyPath, assemblyDir, verbose);

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            // First check the target assembly's directory
            var localPath = Path.Combine(assemblyDir, name.Name + ".dll");
            if (File.Exists(localPath))
            {
                if (verbose)
                    Console.Error.WriteLine($"[DEBUG] AssemblyResolve: {name.Name} → {localPath} (local)");
                return context.LoadFromAssemblyPath(localPath);
            }

            // Then check deps.json mapped paths
            if (assemblyMap.TryGetValue(name.Name!, out var mappedPath) && File.Exists(mappedPath))
            {
                if (verbose)
                    Console.Error.WriteLine($"[DEBUG] AssemblyResolve: {name.Name} → {mappedPath} (deps.json)");
                return context.LoadFromAssemblyPath(mappedPath);
            }

            if (verbose)
                Console.Error.WriteLine($"[DEBUG] AssemblyResolve: {name.Name} → NOT FOUND");
            return null;
        };

        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

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
                var cctorType = ResolveType(assembly, cctorTypeName, verbose);
                if (cctorType is not null)
                {
                    if (verbose)
                        Console.Error.WriteLine($"[DEBUG] Running cctor for: {cctorType.FullName}");
                    RuntimeHelpers.RunClassConstructor(cctorType.TypeHandle);
                }
                else if (verbose)
                {
                    Console.Error.WriteLine($"[DEBUG] WARNING: Could not resolve cctor type: {cctorTypeName}");
                }
            }

            // Resolve the method
            var resolver = new MethodResolver(assembly);
            var method = resolver.ResolveMethod(typeName, methodName, typeParams, classTypeParams);

            if (method is null)
            {
                if (verbose)
                {
                    var types = assembly.GetTypes().Where(t => t.Name.Contains(typeName ?? "")).Take(5);
                    Console.Error.WriteLine($"[DEBUG] Possible types: {string.Join(", ", types.Select(t => t.FullName))}");
                }
                Console.Error.WriteLine($"Could not resolve method: {methodName}");
                return 1;
            }

            if (tier1)
            {
                var parameters = method.GetParameters();
                var invokeArgs = new object?[parameters.Length];
                object? target = method.IsStatic ? null : TryCreateInstance(method.DeclaringType!);

                void InvokeN(int count)
                {
                    for (int i = 0; i < count; i++)
                    {
                        try { method.Invoke(target, invokeArgs); }
                        catch { /* Expected - args are null/default */ }
                    }
                }

                if (verbose)
                    Console.Error.WriteLine("[DEBUG] Phase 1: Invoking to trigger Tier-0 → Instrumented Tier-0...");
                InvokeN(50);
                Thread.Sleep(1000);

                if (verbose)
                    Console.Error.WriteLine("[DEBUG] Phase 2: Invoking to trigger Instrumented Tier-0 → Tier-1...");
                InvokeN(50);
                Thread.Sleep(2000);

                if (verbose)
                    Console.Error.WriteLine("[DEBUG] Phase 3: Final invocations to ensure Tier-1 is installed...");
                InvokeN(50);
                Thread.Sleep(1000);
            }
            else
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                if (verbose)
                    Console.Error.WriteLine($"[DEBUG] PrepareMethod completed for {method.DeclaringType?.FullName}:{method.Name}");

                // Also invoke the method to ensure all code paths are JIT-compiled.
                // PrepareMethod alone may not trigger DOTNET_JitDisasm output for
                // methods from dynamically loaded assemblies.
                var parameters = method.GetParameters();
                var invokeArgs = new object?[parameters.Length];
                object? target = method.IsStatic ? null : TryCreateInstance(method.DeclaringType!);
                try { method.Invoke(target, invokeArgs); }
                catch { /* Expected - args are null/default */ }
                if (verbose)
                    Console.Error.WriteLine("[DEBUG] Method invocation completed");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static object? TryCreateInstance(Type type)
    {
        try
        {
            return RuntimeHelpers.GetUninitializedObject(type);
        }
        catch
        {
            return null;
        }
    }

    private static Type? ResolveType(Assembly assembly, string typeName, bool verbose = false)
    {
        // Try direct lookup in target assembly first
        var type = assembly.GetType(typeName);
        if (type is not null) return type;

        // Try searching all types in target assembly
        foreach (var t in assembly.GetTypes())
        {
            if (t.FullName == typeName || t.Name == typeName)
            {
                return t;
            }
        }

        // Search referenced assemblies (types often come from other assemblies,
        // e.g., Nethermind.Core types referenced from Nethermind.Evm)
        foreach (var refName in assembly.GetReferencedAssemblies())
        {
            try
            {
                var refAssembly = Assembly.Load(refName);
                type = refAssembly.GetType(typeName);
                if (type is not null)
                {
                    if (verbose)
                        Console.Error.WriteLine($"[DEBUG] Resolved cctor type '{typeName}' from referenced assembly {refName.Name}");
                    return type;
                }

                // Try by short name
                foreach (var t in refAssembly.GetTypes())
                {
                    if (t.FullName == typeName || t.Name == typeName)
                    {
                        if (verbose)
                            Console.Error.WriteLine($"[DEBUG] Resolved cctor type '{typeName}' from referenced assembly {refName.Name} (by scan)");
                        return t;
                    }
                }
            }
            catch
            {
                // Skip assemblies that can't be loaded
            }
        }

        // Try Type.GetType as last resort (requires assembly-qualified names for cross-assembly)
        return Type.GetType(typeName);
    }

    /// <summary>
    /// Build a mapping from assembly name to file path using the deps.json file.
    /// Resolves NuGet package references via the global packages cache.
    /// </summary>
    private static Dictionary<string, string> BuildAssemblyMap(string assemblyPath, string assemblyDir, bool verbose)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (!File.Exists(depsPath))
            return map;

        string nugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        try
        {
            using var stream = File.OpenRead(depsPath);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (!root.TryGetProperty("targets", out var targets))
                return map;

            // Get the first (and usually only) target
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var package in target.Value.EnumerateObject())
                {
                    if (!package.Value.TryGetProperty("runtime", out var runtime))
                        continue;

                    foreach (var dll in runtime.EnumerateObject())
                    {
                        string dllRelativePath = dll.Name; // e.g. "lib/net10.0/Nethermind.Int256.dll"
                        string asmName = Path.GetFileNameWithoutExtension(dllRelativePath);

                        // Skip if already in the local directory
                        if (File.Exists(Path.Combine(assemblyDir, asmName + ".dll")))
                            continue;

                        // Resolve from NuGet cache: packages/{id}/{version}/{path}
                        string packageId = package.Name; // e.g. "Nethermind.Numerics.Int256/1.4.0"
                        string[] parts = packageId.Split('/');
                        if (parts.Length == 2)
                        {
                            string fullPath = Path.Combine(nugetPackages, parts[0].ToLowerInvariant(), parts[1], dllRelativePath);
                            if (File.Exists(fullPath))
                            {
                                map[asmName] = fullPath;
                                if (verbose)
                                    Console.Error.WriteLine($"[DEBUG] Deps map: {asmName} → {fullPath}");
                            }
                        }
                    }
                }
                break; // Only process the first target
            }
        }
        catch (Exception ex)
        {
            if (verbose)
                Console.Error.WriteLine($"[DEBUG] Failed to parse deps.json: {ex.Message}");
        }

        return map;
    }
}

