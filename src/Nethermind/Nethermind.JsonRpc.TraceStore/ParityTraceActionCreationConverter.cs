// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json.Converters;

namespace Nethermind.JsonRpc.TraceStore;

public class ParityTraceActionCreationConverter : CustomCreationConverter<ParityTraceAction>
{
    public override ParityTraceAction Create(Type objectType) => new() { Result = null };
}
