// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Proof;

/// <summary>
/// Response payload of <c>proof_call</c>.
/// </summary>
/// <remarks>
/// On clean success, <see cref="Result"/> holds the EVM return data and <see cref="Error"/> is null.
/// On any in-VM failure (REVERT, out-of-gas, invalid jump, ...) <see cref="Error"/> is set and
/// <see cref="Result"/> is null; the error object mirrors the JSON-RPC error envelope used by
/// <c>eth_call</c>:
/// <list type="bullet">
///   <item><description>REVERT: <c>code = ExecutionReverted</c>, <c>message = "execution reverted[: reason]"</c>, <c>data = "0x..." revert payload</c></description></item>
///   <item><description>Out-of-gas / invalid jump / etc.: <c>code = ExecutionError</c>, <c>message = "&lt;reason&gt;"</c>, no <c>data</c></description></item>
/// </list>
/// Pre-VM failures (insufficient funds, invalid signature, ...) are surfaced as a JSON-RPC envelope
/// error rather than this payload — matching <c>eth_call</c> exactly.
/// <para>
/// The deliberate divergence from <c>eth_call</c>: in-VM failures (revert, OOG, ...) succeed at the
/// JSON-RPC layer here and carry the same error object in the payload, alongside the witness, so a
/// verifier can independently re-prove the failure. <c>eth_call</c> would fail those at the envelope
/// level and the witness would have nowhere to live.
/// </para>
/// <see cref="Witness"/> is the geth/reth-compatible execution witness — flat lists of trie-node RLP,
/// loaded bytecode, accessed keys, and RLP-encoded block headers emitted in ascending block-number
/// order (any block whose hash was read via <c>BLOCKHASH</c> comes first, and the executed-against
/// block header is always the last entry). The witness is captured even on in-VM failure.
/// </remarks>
public class CallResultWithProof : IDisposable
{
    public byte[]? Result { get; init; }

    public Error? Error { get; init; }

    public required Witness Witness { get; init; }

    /// <remarks>
    /// The contained <see cref="Witness"/> holds pooled buffers (<c>IOwnedReadOnlyList&lt;byte[]&gt;</c>);
    /// disposing the result returns them to the pool. <c>ResultWrapper&lt;T&gt;.Dispose</c> propagates here
    /// when the JSON-RPC layer releases the response, so callers do not need to dispose manually.
    /// </remarks>
    public void Dispose() => Witness.Dispose();

    /// <summary>
    /// Translates a <see cref="SingleCallWitnessResult"/> into the JSON-RPC response wrapper.
    /// </summary>
    /// <remarks>
    /// Pre-VM input errors are surfaced as a failed <see cref="ResultWrapper{T}"/> (mirroring
    /// <c>eth_call</c>), because there is no witness to attach to a transaction that never executed.
    /// In-VM failures (revert, out-of-gas, etc.) succeed at the JSON-RPC layer and carry an in-payload
    /// error object plus the witness — exactly the state the verifier needs to re-prove the failure.
    /// </remarks>
    public static ResultWrapper<CallResultWithProof> FromWitnessResult(SingleCallWitnessResult result, ulong txGasLimit = 0)
    {
        if (result.InputError)
        {
            // SingleCallWitnessCollector populates Witness even on input errors; the envelope-level Fail
            // path has nowhere to attach it, so dispose now to release its pooled buffers. The message
            // is wrapped the same way eth_call wraps it ("err: <inner> (supplied gas <gas>)") so a
            // caller that already handles eth_call's envelope can reuse the parser.
            result.Witness.Dispose();
            string message = result.Error is null
                ? "execution failed"
                : ErrorWrapper.EthCall(result.Error, txGasLimit);
            return ResultWrapper<CallResultWithProof>.Fail(message, ErrorCodes.ExecutionError);
        }

        Error? error =
            result.ExecutionReverted ? BuildRevertError(result.Output, result.Error)
            : result.Error is not null ? new Error { Code = ErrorCodes.ExecutionError, Message = result.Error }
            : null;

        return ResultWrapper<CallResultWithProof>.Success(new CallResultWithProof
        {
            Result = error is null ? result.Output : null,
            Error = error,
            Witness = result.Witness,
        });
    }

    /// <remarks>The message is built by the shared <see cref="TransactionSubstate.BuildRevertMessage"/>
    /// so it matches <c>eth_call</c> byte-for-byte. The raw payload is hex-encoded in <c>data</c> —
    /// empty payloads render as <c>"0x"</c> (matching <c>eth_call</c>), not omitted, so consumers
    /// can branch on the presence of <c>data</c> without special-casing the empty case.</remarks>
    private static Error BuildRevertError(byte[]? output, string? tracerError) =>
        new()
        {
            Code = ErrorCodes.ExecutionReverted,
            Message = TransactionSubstate.BuildRevertMessage(output, tracerError),
            Data = output is null ? null : output.ToHexString(true),
        };
}
