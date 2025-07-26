// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public sealed class ComposedJsonRpcValidator : IJsonRpcValidator
{
    private readonly IEnumerable<IJsonRpcValidator> _validators;

    public ComposedJsonRpcValidator(IEnumerable<IJsonRpcValidator> validators)
    {
        _validators = validators;
    }

    public bool IsValid(JsonRpc.Request request, JsonRpc.Response response) => _validators.All(validator => validator.IsValid(request, response));
}
