// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public class NullJsonRpcValidator : IJsonRpcValidator
{
    public bool IsValid(JsonDocument? document) => true;
}
