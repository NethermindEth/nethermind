// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.BlockValidation.Data;
using Nethermind.BlockValidation.Handlers;
using Nethermind.JsonRpc;

namespace Nethermind.BlockValidation.Modules.Flashbots;

public class FlashbotsRpcModule : IFlashbotsRpcModule
{
    private readonly ValidateSubmissionHandler _validateSubmissionHandler;

    public FlashbotsRpcModule(ValidateSubmissionHandler validateSubmissionHandler)
    {
        _validateSubmissionHandler = validateSubmissionHandler;
    }

    Task<ResultWrapper<BlockValidationResult>> IFlashbotsRpcModule.flashbots_validateBuilderSubmissionV3(BuilderBlockValidationRequest @params) =>
        _validateSubmissionHandler.ValidateSubmission(@params);

}
