// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Data;
public partial class BuilderBlockValidationRequest
{
    public BidTrace? Message { get; set; }
    public ExecutionPayloadForRpc? ExecutionPayload { get; set; }
    public uint RegisteredGasLimit { get; set; }
    public Keccak? WithdrawalsRoot { get; set; }
    public byte[]? Signature { get; set; }
}
