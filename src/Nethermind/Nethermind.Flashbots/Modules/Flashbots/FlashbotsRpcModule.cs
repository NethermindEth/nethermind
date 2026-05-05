// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Flashbots.Data;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc;

namespace Nethermind.Flashbots.Modules.Flashbots;

public class FlashbotsRpcModule(ValidateSubmissionHandler validateSubmissionHandler) : IFlashbotsRpcModule
{
    private readonly ValidateSubmissionHandler _validateSubmissionHandler = validateSubmissionHandler;

    Task<ResultWrapper<FlashbotsResult>> IFlashbotsRpcModule.flashbots_validateBuilderSubmissionV3(BuilderBlockValidationRequest @params) =>
        _validateSubmissionHandler.ValidateSubmission(@params);
}
