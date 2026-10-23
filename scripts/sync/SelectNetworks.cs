#!/usr/bin/env dotnet
#:include Sync.Helpers.cs
using System.Text.Json.Nodes;

try
{
    var input = Console.In.ReadToEnd();
    var filter = args.FirstOrDefault() ?? "";
    if (filter.Length == 0) Console.Write(input);
    else Console.WriteLine(Sync.Select(JsonNode.Parse(input)!.AsArray(), filter).ToJsonString());
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
