// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.EraE.JsonRpc;

[RpcModule(ModuleType.Admin)]
public interface IEraAdminRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Exports a range of historic blocks in erae format.",
        EdgeCaseHint = "",
        ExampleResponse = "\"Export task started.\"",
        IsImplemented = true)]
    Task<ResultWrapper<string>> admin_exportEraHistory(
        [JsonRpcParameter(Description = "Destination path to export to.", ExampleValue = "/tmp/eraeexportdir")]
        string destinationPath,
        [JsonRpcParameter(Description = "Start block to export from.", ExampleValue = "0")]
        long from,
        [JsonRpcParameter(Description = "Last block to export to. Set to 0 to export to head.", ExampleValue = "1000000")]
        long to
    );

    [JsonRpcMethod(
        Description = "Imports a range of historic blocks from an erae directory.",
        EdgeCaseHint = "",
        ExampleResponse = "\"Import task started.\"",
        IsImplemented = true)]
    Task<ResultWrapper<string>> admin_importEraHistory(
        [JsonRpcParameter(Description = "Source path to import from.", ExampleValue = "/tmp/eraedir")]
        string sourcePath,
        [JsonRpcParameter(Description = "Start block to import. Set to 0 for first available.", ExampleValue = "0")]
        long from = 0,
        [JsonRpcParameter(Description = "End block to import. Set to 0 for last available.", ExampleValue = "0")]
        long to = 0,
        [JsonRpcParameter(Description = "Accumulator file to trust. Set to null to skip accumulator verification.", ExampleValue = "null")]
        string? accumulatorFile = null
    );
}
