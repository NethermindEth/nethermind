// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs;
using Nethermind.Taiko.TaikoSpec;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

public class TaikoEngineApiForkWindowTests
{
    [Test]
    public void NewPayloadV3_at_Unzen_is_unsupported_while_missing_requests_are_invalid()
    {
        TaikoUnzenReleaseSpec spec = new();
        TaikoExecutionPayloadV3 payload = new();
        ExecutionPayloadParams<TaikoExecutionPayloadV3> parameters = new(payload, [], Hash256.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.ValidateForkOnNewPayload(new TestSingleReleaseSpecProvider(spec), EngineApiVersions.NewPayload.V3), Is.False);
            Assert.That(parameters.ValidateParams(spec, EngineApiVersions.NewPayload.V3, out string? error), Is.EqualTo(Nethermind.Merge.Plugin.Data.ValidationResult.Fail));
            Assert.That(error, Is.EqualTo("Execution requests must be set"));
        }
    }
}
