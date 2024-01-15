// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public class ComposedJsonRpcValidator : IJsonRpcValidator
{
    private readonly IEnumerable<IJsonRpcValidator> _validators;

    public ComposedJsonRpcValidator(IEnumerable<IJsonRpcValidator> validators)
    {
        _validators = validators;
    }

    public bool IsValid(JsonRpc request, JsonDocument? document) => _validators.All(validator => validator.IsValid(request, document));
}
