// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Api.Common;

namespace Nethermind.BeaconChain.Api.Endpoints;

/// <summary>
/// <c>/eth/v1/validator/*</c>: this driver runs no validator client and performs no attesting or
/// proposing duties, so every validator-duty endpoint answers 501 rather than pretending to.
/// </summary>
internal static class ValidatorEndpoints
{
    private const string NonAttestingMessage =
        "This node is non-attesting: it drives the execution layer through the engine API but performs no validator duties, so /eth/v1/validator/* is not served.";

    public static void Map(WebApplication app, BeaconApiContext ctx) =>
        app.MapMethods("/eth/v1/validator/{**path}", ["GET", "POST"],
            c => ApiErrors.Write(c, StatusCodes.Status501NotImplemented, NonAttestingMessage, c.RequestAborted));
}
