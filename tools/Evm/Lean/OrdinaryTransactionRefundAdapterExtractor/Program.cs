// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

if (args.Length is not (2 or 3) || args[0] is not ("--audit" or "--extract" or "--check"))
{
    Console.Error.WriteLine("Usage: --audit <repository-root> | --extract|--check <repository-root> [artifact-directory]. Verification covers only the conditional standard-mainnet refund-helper boundary.");
    return 2;
}

try
{
    if (args[0] == "--audit")
    {
        SourceAdmission.AdmissionResult admission = SourceAdmission.Read(args[1]);
        Console.WriteLine(JsonSerializer.Serialize(admission, CompilerReferences.JsonOptions));
    }
    else
    {
        string output = args.Length == 3 ? args[2] : Path.Combine(args[1], SourceAdmission.PackagePath, "Generated");
        if (args[0] == "--extract") Artifacts.Extract(args[1], output);
        else Artifacts.Check(args[1], output);
        Console.WriteLine(args[0] == "--extract" ? "Emitted source-admitted refund artifacts." : "Refund compiled-source/artifact check passed.");
    }
    return 0;
}
catch (Exception exception) when (exception is AdmissionException or IOException or JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
