#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Sync.Helpers.cs
using System.Text.Json.Nodes;

try
{
    var input = Console.In.ReadToEnd().TrimEnd('\r', '\n');
    var errors = Sync.ShapeErrors(JsonNode.Parse(input)!.AsArray()).ToArray();
    foreach (var error in errors) Console.Error.WriteLine($"::error title=Sync matrix::{error}");
    if (errors.Length > 0) return 1;
    Console.WriteLine(input);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 143;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
