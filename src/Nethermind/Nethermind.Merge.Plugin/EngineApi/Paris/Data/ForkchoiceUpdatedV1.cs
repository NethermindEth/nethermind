// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Data;

public record ForkchoiceUpdated<TForkchoiceState, TPayloadAttributes>(TForkchoiceState ForkchoiceState, TPayloadAttributes? PayloadAttributes)
    where TForkchoiceState : ForkchoiceStateV1
    where TPayloadAttributes : PayloadAttributesV1;

public sealed record ForkchoiceUpdatedV1(ForkchoiceStateV1 ForkchoiceState, PayloadAttributesV1? PayloadAttributes)
    : ForkchoiceUpdated<ForkchoiceStateV1, PayloadAttributesV1>(ForkchoiceState, PayloadAttributes);
