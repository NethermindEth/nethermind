// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

public interface IExecutionEngineManager
{
    void Initialize();
    Task ProcessNewDerivedPayloadAttributes(PayloadAttributesRef payloadAttributes);
    Task ProcessNewP2PExecutionPayload(ExecutionPayloadV3 executionPayloadV3);
}
