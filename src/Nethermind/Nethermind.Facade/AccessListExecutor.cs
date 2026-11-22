// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Facade;

internal static class AccessListExecutor
{
    // Convergence loop: mirrors Geth's AccessList() — run with the current AL, discover touched
    // slots, repeat until the AL stabilizes. Gas and error come from the final (warm) run, so
    // cold-read overcounting is eliminated and OOG due to AL intrinsic cost is surfaced.
    // Starts from the caller-supplied AL so user-provided entries are preserved and counted.
    internal static (CallOutput Output, EvmExceptionType Failure) Create(IWorldState nonceSource, BlockHeader header, Transaction tx, IReleaseSpec spec,
        IEthereumEcdsa ecdsa, ulong chainId, bool optimize, Func<ITxTracer, TransactionResult> run,
        CancellationToken cancellationToken, int maxIterations = int.MaxValue)
    {
        // Loop-invariant: the addresses to filter from the discovered AL depend only on header
        // and tx, neither of which change between iterations. Compute once and reuse.
        FrozenSet<AddressAsKey> precompiles = spec.Precompiles;
        int bufferSize = (optimize ? 3 + (tx.AuthorizationList?.Length ?? 0) : 1) + precompiles.Count;
        Address[] addressBuffer = new Address[bufferSize];
        int addressCount = FillAddressesToOptimize(addressBuffer, nonceSource, header, tx, optimize, precompiles, ecdsa, chainId);
        if (addressCount < addressBuffer.Length) Array.Resize(ref addressBuffer, addressCount);

        // Tracing access charges every account and slot a run touches as warm, so a branch that depends on gas can
        // go another way than in a transaction carrying the list. The optimized list is instead collected, as Geth
        // does, from what opcodes name in runs that pay the real access costs.
        AccessTxTracer? accessTracer = optimize ? null : new(addressBuffer);
        OpcodeAccessListTracer? opcodeTracer = optimize ? new(addressBuffer) : null;
        AccessList? previousAccessList = tx.AccessList;
        if (opcodeTracer is not null)
        {
            opcodeTracer.Reset(previousAccessList);
            previousAccessList = opcodeTracer.BuildAccessList();
        }

        CallOutputTracer outputTracer = new OutputlessCallTracer();
        CancellationTxTracer tracer = new CompositeTxTracer(outputTracer, accessTracer ?? (ITxTracer)opcodeTracer!).WithCancellation(cancellationToken);
        TransactionResult result;
        bool stop;
        int iterations = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (iterations++ == maxIterations)
                return (new() { Error = "Access list did not converge within the iteration limit.", InputError = true }, default);
            accessTracer?.Reset();
            opcodeTracer?.Reset(previousAccessList);
            outputTracer.Reset();
            tx.AccessList = previousAccessList;
            result = run(tracer);
            AccessList? discovered = opcodeTracer?.BuildAccessList() ?? accessTracer!.AccessList;
            stop = !result.TransactionExecuted || HasConverged(previousAccessList, discovered);
            previousAccessList = discovered;
        } while (!stop);

        return (BuildCallOutput(result, outputTracer, previousAccessList), result.EvmExceptionType);
    }

    private static CallOutput BuildCallOutput(TransactionResult result, CallOutputTracer outputTracer, AccessList? accessList)
    {
        bool executionReverted = result.EvmExceptionType == EvmExceptionType.Revert;
        // Geth always surfaces plain "execution reverted" for eth_createAccessList,
        // regardless of whether the revert payload carries a decoded reason.
        string? error = executionReverted
            ? "execution reverted"
            : result.GetErrorMessage(outputTracer.Error);

        return new CallOutput
        {
            Error = error,
            GasSpent = outputTracer.GasSpent,
            OperationGas = outputTracer.OperationGas,
            InputError = !result.TransactionExecuted,
            ExecutionReverted = executionReverted,
            AccessList = accessList,
        };
    }

    /// <summary>Collects the outcome of a run without its output, which eth_createAccessList does not report.</summary>
    private sealed class OutputlessCallTracer : CallOutputTracer, ITxOutputTracingFilter
    {
        public bool IsTracingOutput => false;
    }

    private static int FillAddressesToOptimize(Span<Address> buffer, IWorldState nonceSource, BlockHeader header, Transaction tx,
        bool optimize, FrozenSet<AddressAsKey> precompiles, IEthereumEcdsa ecdsa, ulong chainId)
    {
        int idx;
        if (!optimize)
        {
            buffer[0] = header.GasBeneficiary;
            idx = 1;
        }
        else
        {
            // EIP-2930: sender, recipient and gas beneficiary are implicitly accessed,
            // so excluding them keeps the returned access list minimal.
            // The nonce comes from the executing world state: the header's state root does not
            // reflect an unmerkleized override, so a nonce override would resolve to the wrong
            // CREATE address here.
            UInt256 senderNonce = tx.IsContractCreation ? nonceSource.GetNonce(tx.SenderAddress) : UInt256.Zero;
            buffer[0] = tx.SenderAddress;
            buffer[1] = tx.GetRecipient(senderNonce);
            buffer[2] = header.GasBeneficiary;
            idx = 3;

            // Geth also excludes the authority of every EIP-7702 authorization it does not skip outright.
            foreach (AuthorizationTuple authorization in tx.AuthorizationList ?? [])
            {
                if ((!authorization.ChainId.IsZero && authorization.ChainId != chainId)
                    || authorization.Nonce == ulong.MaxValue
                    || new UInt256(authorization.AuthoritySignature.SAsSpan, isBigEndian: true) > SecP256k1Curve.HalfN
                    || authorization.AuthoritySignature.V - Signature.VOffset > 1)
                    continue;

                if ((authorization.Authority ??= ecdsa.RecoverAddress(authorization)) is { } authority)
                    buffer[idx++] = authority;
            }
        }

        foreach (AddressAsKey p in precompiles)
            buffer[idx++] = p.Value;

        return idx;
    }

    /// <summary>Collects the access list Geth's access-list tracer records, on top of the list a run starts from.</summary>
    /// <remarks>
    /// Records the target of a call, the account an account-reading opcode names, and the slot an <c>SLOAD</c> or
    /// <c>SSTORE</c> names in the executing account. The EVM also warms accounts no opcode names, such as delegation
    /// targets and created contracts, and Geth leaves those out. The tracer does not trace access, so the run it
    /// observes pays the same access costs as a transaction carrying the list.
    /// </remarks>
    private sealed class OpcodeAccessListTracer(Address[] excludedAddresses) : TxTracer
    {
        // Geth orders the list by address and each address's keys ascending; the addresses are sorted when it is built.
        private readonly Dictionary<AddressAsKey, SortedSet<UInt256>> _accessList = [];
        private Instruction _opcode;
        private Address? _executingAccount;

        public override bool IsTracingInstructions => true;
        public override bool IsTracingStack => true;

        public void Reset(AccessList? startingList)
        {
            _accessList.Clear();
            if (startingList is null) return;

            foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in startingList)
            {
                SortedSet<UInt256> keys = GetStorageKeys(address);
                foreach (UInt256 storageKey in storageKeys)
                    keys.Add(storageKey);
            }
        }

        /// <returns>The collected list, without the excluded addresses that hold no storage key.</returns>
        public AccessList BuildAccessList()
        {
            Address[] addresses = new Address[_accessList.Count];
            int index = 0;
            foreach (AddressAsKey address in _accessList.Keys)
                addresses[index++] = address;
            addresses.AsSpan().Sort(new AddressComparer());

            AccessList.Builder builder = new();
            foreach (Address address in addresses)
            {
                SortedSet<UInt256> storageKeys = _accessList[address];
                if (storageKeys.Count == 0 && Array.IndexOf(excludedAddresses, address) >= 0)
                    continue;

                builder.AddAddress(address);
                foreach (UInt256 storageKey in storageKeys)
                    builder.AddStorage(storageKey);
            }

            return builder.Build();
        }

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            _opcode = opcode;
            _executingAccount = env.ExecutingAccount;
        }

        public override void SetOperationStack(TraceStack stack)
        {
            switch (_opcode)
            {
                case Instruction.SLOAD or Instruction.SSTORE when stack.Count >= 1:
                    GetStorageKeys(_executingAccount!).Add(stack.PeekUInt256(0));
                    break;
                // Geth requires five items for every call variant, although DELEGATECALL and STATICCALL take six.
                case Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL when stack.Count >= 5:
                    GetStorageKeys(stack.PeekAddress(1));
                    break;
                case Instruction.BALANCE or Instruction.EXTCODESIZE or Instruction.EXTCODECOPY or Instruction.EXTCODEHASH or Instruction.SELFDESTRUCT when stack.Count >= 1:
                    GetStorageKeys(stack.PeekAddress(0));
                    break;
            }
        }

        private SortedSet<UInt256> GetStorageKeys(Address address)
        {
            if (!_accessList.TryGetValue(address, out SortedSet<UInt256>? keys))
                _accessList[address] = keys = [];

            return keys;
        }
    }

    private static bool HasConverged(AccessList? previous, AccessList? discovered)
    {
        // Count comparison is sufficient because each run starts from `previous`: WarmUp(tx.AccessList)
        // pre-populates the warm-address set with its entries, and OpcodeAccessListTracer is seeded with
        // them, making `discovered` monotonically non-decreasing (discovered ⊇ previous). Equal counts
        // therefore imply equal content.
        (int addrs, int keys) previousCount = previous?.Count ?? (0, 0);
        (int addrs, int keys) discoveredCount = discovered?.Count ?? (0, 0);
        return previousCount == discoveredCount;
    }
}
