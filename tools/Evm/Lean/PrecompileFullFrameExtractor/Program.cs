// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Lean.PrecompileFullFrameExtractor;

try
{
    if (args.Length != 3 || args[0] is not ("extract" or "validate"))
    {
        Console.Error.WriteLine("Usage: PrecompileFullFrameExtractor <extract|validate> <repository-root> <artifact-directory>");
        return 2;
    }

    Artifacts artifacts = Profile.Build(args[1]);
    if (args[0] == "extract") Profile.Write(args[2], artifacts);
    else Profile.Validate(args[2], artifacts);
    Console.WriteLine($"Stage C: {artifacts.Ir.Sources.Length} sources, {artifacts.Ir.Members.Length} members, " +
        $"{artifacts.Ir.Dependencies.Length} dependencies, {artifacts.Ir.Oracles.Length} oracles, {artifacts.Ir.Branches.Length} branches.");
    Console.WriteLine($"IR {Profile.Hash(artifacts.IrBytes)}");
    Console.WriteLine($"Manifest {Profile.Hash(artifacts.ManifestBytes)}");
    Console.WriteLine($"Lean {Profile.Hash(artifacts.LeanBytes)}");
    return 0;
}
catch (Exception exception) when (exception is ExtractionException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
