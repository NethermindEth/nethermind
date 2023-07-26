// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public class TracerValidator : IJsonRpcValidator
{
    private readonly IJsonRpcValidator _validator;
    private readonly string _tracesFilePath;

    public TracerValidator(IJsonRpcValidator validator, string tracesFilePath)
    {
        _validator = validator;
        _tracesFilePath = tracesFilePath;
    }

    public bool IsValid(JsonDocument? document)
    {
        using (StreamWriter sw = File.Exists(_tracesFilePath) ? File.CreateText(_tracesFilePath) : File.AppendText(_tracesFilePath))
        {
            sw.WriteLine(document?.RootElement.ToString() ?? "null");
        }

        return _validator.IsValid(document);
    }
}
