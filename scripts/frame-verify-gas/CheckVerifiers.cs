#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include VerifierArtifacts.Helpers.cs

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: CheckVerifiers.cs <root> <label>...");
    return 2;
}
return VerifierArtifacts.Check(args[0], args[1..]);
