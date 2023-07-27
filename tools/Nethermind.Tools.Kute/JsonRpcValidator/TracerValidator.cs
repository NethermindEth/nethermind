// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public class TracerValidator : IJsonRpcValidator
{
    private readonly string _tracesFilePath;

    public TracerValidator(string tracesFilePath)
    {
        _tracesFilePath = tracesFilePath;
    }

    public bool IsValid(JsonRpc request, JsonDocument? response)
    {
        using StreamWriter sw = File.Exists(_tracesFilePath)
            ? File.AppendText(_tracesFilePath)
            : File.CreateText(_tracesFilePath);

        sw.WriteLine(response?.RootElement.ToString() ?? "null");

        return true;
    }
}
