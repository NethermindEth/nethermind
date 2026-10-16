// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Grpc.Core;

namespace Nethermind.Eez.Attester;

internal readonly record struct StreamingProtocolRules(StreamingSettlementProfile Profile)
{
    internal bool ChargesWireFrames => Profile == StreamingSettlementProfile.RollupXNative;
    internal bool ReturnsBoundControlErrors => Profile == StreamingSettlementProfile.RollupXNative;
    internal bool EvictsDetachedSessions => Profile is StreamingSettlementProfile.RollupXNative or StreamingSettlementProfile.Rollup0Conditional;
    internal StatusCode ZeroRollup => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.InvalidArgument : StatusCode.Unimplemented;
    internal StatusCode UnboundControl => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.PermissionDenied : StatusCode.InvalidArgument;
    internal StatusCode InvalidRange => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.FailedPrecondition : StatusCode.InvalidArgument;
    internal StatusCode InvalidAncestor => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.FailedPrecondition : StatusCode.InvalidArgument;
    internal StatusCode RetiredAttachment => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.PermissionDenied : StatusCode.Aborted;
    internal StatusCode MessageSize => Profile == StreamingSettlementProfile.RollupXNative ? StatusCode.OutOfRange : StatusCode.ResourceExhausted;
}
