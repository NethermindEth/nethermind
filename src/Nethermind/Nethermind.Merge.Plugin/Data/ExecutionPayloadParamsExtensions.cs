// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Data;

public static class ExecutionPayloadParamsExtensions
{
    /// <summary>
    /// Logs information about ParentBeaconBlockRoot before validation.
    /// </summary>
    public static void LogParentBeaconBlockRoot(this IExecutionPayloadParams executionPayloadParams, ILogger logger)
    {
        ExecutionPayload executionPayload = executionPayloadParams.ExecutionPayload;
        Hash256? parentBeaconBlockRoot = executionPayloadParams.ParentBeaconBlockRoot;

        // Log when executionPayload has a non-null ParentBeaconBlockRoot
        if (executionPayload.ParentBeaconBlockRoot is not null)
        {
            if (logger.IsInfo)
            {
                logger.Info($"Execution payload has non-null ParentBeaconBlockRoot: {executionPayload.ParentBeaconBlockRoot}");
            }

            // Log warning when executionPayload.ParentBeaconBlockRoot doesn't match the input parentBeaconBlockRoot
            if (parentBeaconBlockRoot is not null && executionPayload.ParentBeaconBlockRoot != parentBeaconBlockRoot)
            {
                if (logger.IsWarn)
                {
                    logger.Warn($"Execution payload ParentBeaconBlockRoot ({executionPayload.ParentBeaconBlockRoot}) does not match input parentBeaconBlockRoot ({parentBeaconBlockRoot})");
                }
            }
        }

        Hash256? finalParentBeaconBlockRoot = parentBeaconBlockRoot ?? executionPayload.ParentBeaconBlockRoot;

        // Log error when finalParentBeaconBlockRoot is null
        if (finalParentBeaconBlockRoot is null)
        {
            if (logger.IsError)
            {
                logger.Error("finalParentBeaconBlockRoot is null");
            }
        }
    }
}

