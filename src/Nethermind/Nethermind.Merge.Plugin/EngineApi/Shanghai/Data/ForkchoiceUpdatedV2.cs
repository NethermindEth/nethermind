// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.EngineApi.Paris.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

public record ForkchoiceUpdatedV2(ForkchoiceStateV1 ForkchoiceState, PayloadAttributesV2? PayloadAttributes)
    : ForkchoiceUpdated<ForkchoiceStateV1, PayloadAttributesV2>(ForkchoiceState, PayloadAttributes);
