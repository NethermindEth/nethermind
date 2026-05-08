// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.Tools.Kute.JsonRpcValidator;

public sealed class BatchJsonRpcValidator : IJsonRpcValidator
{
    private readonly IJsonRpcValidator _singleValidator;

    public BatchJsonRpcValidator(IJsonRpcValidator singleValidator)
    {
        _singleValidator = singleValidator;
    }

    public bool IsValid(JsonRpc.Request request, JsonRpc.Response response)
    {
        switch (request)
        {
            case JsonRpc.Request.Single single:
                return _singleValidator.IsValid(single, response);
            case JsonRpc.Request.Batch batch:
                {
                    if (response.Json is not JsonArray)
                    {
                        return false;
                    }

                    var responses = response.Json
                        .AsArray()
                        .Select(r => new JsonRpc.Response(r!))
                        .Where(r => r.Id is not null)
                        .OrderBy(r => r.Id)
                        .ToList();

                    var requests = batch
                        .Items()
                        .Select(r => r!)
                        .Where(r => r.Id is not null)
                        .OrderBy(r => r.Id)
                        .ToList();

                    if (responses.Count != requests.Count) return false;

                    foreach (var (req, res) in requests.Zip(responses))
                    {
                        if (req.Id != res.Id) return false;
                        if (_singleValidator.IsInvalid(req, res)) return false;
                    }

                    return true;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}
