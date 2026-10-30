// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static partial class StatelessExecutor
{
    /// <summary>Validates the block a <see cref="TrustedInput"/> carries, as <see cref="Execute(ReadOnlySpan{byte})"/>
    /// validates a stateless input.</summary>
    /// <returns>The encoded <see cref="StatelessValidationResult"/>, whose root is the block hash and whose schema id is
    /// <see cref="TrustedInput.SchemaId"/>.</returns>
    public static byte[] ExecuteTrusted(ReadOnlySpan<byte> data)
    {
        byte[] output = StatelessValidationResult.Encode(_defaultFailureResult);
        FailureOutput = output;
        TrustedPayload payload;

        try
        {
            payload = TrustedInput.Read(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return output;
        }

        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.BlockHash,
            IsSuccess = false,
            ChainId = payload.ChainId,
            SchemaId = TrustedInput.SchemaId
        };
        output = StatelessValidationResult.Encode(result);
        bool success = false;
        FailureOutput = output;

        try
        {
            Block block = payload.GetBlock();
            ISpecProvider specProvider = payload.SpecProvider;
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            InitializeKzg(spec);
            if (TrySetTrustedSenders(block.Transactions, payload, specProvider, spec))
            {
                using Witness witness = payload.Witness;

                // The header's hash is the keccak of the RLP it was decoded from, so it needs no check; its body roots do.
                success = Execute(block, witness, specProvider, validateHashes: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }

        if (success)
        {
            result.IsSuccess = true;
            output = StatelessValidationResult.Encode(result);
        }

        return output;
    }

    /// <summary>Executes the block of <paramref name="input"/> on the host, returning the inputs of its ECRECOVER calls.</summary>
    /// <remarks>An invalid block gives the calls made until it failed, which are as good as any for hints.</remarks>
    internal static IReadOnlyList<byte[]> CollectRecoveryInputs(byte[] input)
    {
        EcRecoverInputCollector collector = new();
        try
        {
            TrustedPayload payload = TrustedInput.Read(input);
            Block block = payload.GetBlock();
            IReleaseSpec spec = payload.SpecProvider.GetSpec(block.Header);
            using Witness witness = payload.Witness;
            if (TryRecoverSenders(block.Transactions, payload.EncodedTransactions, payload.SpecProvider, spec))
                Execute(block, witness, payload.SpecProvider, validateHashes: true, collector);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }

        return collector.Inputs;
    }

    /// <summary>Records the input of every call to the ECRECOVER precompile.</summary>
    private sealed class EcRecoverInputCollector : TxTracer, IBlockTracer
    {
        public EcRecoverInputCollector() => IsTracingActions = true;

        public List<byte[]> Inputs { get; } = [];

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
            ExecutionType callType, bool isPrecompileCall = false)
        {
            if (isPrecompileCall && to == ECRecoverPrecompile.Address) Inputs.Add(input.ToArray());
        }

        public bool IsTracingRewards => false;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

        public void StartNewBlockTrace(Block block) { }

        public ITxTracer StartNewTxTrace(Transaction? tx) => this;

        public void EndTxTrace() { }

        public void EndBlockTrace() { }
    }

    /// <summary>Sets every sender, from the input's sender hints where the guest can check them all at once.</summary>
    /// <remarks>A failed check says nothing about the block, as the hints may be what is wrong, so recovery then decides.</remarks>
    private static bool TrySetTrustedSenders(Transaction[] transactions, in TrustedPayload payload, ISpecProvider specProvider, IReleaseSpec spec)
    {
        bool verified = false;
        if (payload.SenderHints.Length != 0)
            TryVerifySendersInBatch(transactions, payload, specProvider, spec, ref verified);

        return verified || TryRecoverSenders(transactions, payload.EncodedTransactions, specProvider, spec);
    }

    /// <summary>Checks every signature against its sender hint in one call where the guest can, setting the senders and
    /// <paramref name="verified"/> when all match.</summary>
    static partial void TryVerifySendersInBatch(
        Transaction[] transactions, in TrustedPayload payload, ISpecProvider specProvider, IReleaseSpec spec, ref bool verified);
}
