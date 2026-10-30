// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class RollupXObservationDispatch(ICodeInfoRepository inner, IWorldState state,
    Address manager, RollupXObservationClaim claim) : ICodeInfoRepository
{
    private Instruction _opcode = Instruction.CALL;
    private CallbackPrecompile? _pending;
    private int _answered;
    private bool _failed;
    private StackAccessTracker _access;
    private bool _hasAccess;
    private StorageCell? _rootRead;

    public bool IsCodeOverridable => true;

    internal void StartOperation(Instruction opcode) => _opcode = opcode;

    internal void Begin(in StackAccessTracker access)
    {
        _access = access;
        _hasAccess = true;
        if (_rootRead is { } cell) Warm(in cell);
        _rootRead = null;
    }

    internal void End()
    {
        _hasAccess = false;
        _access = default;
        _rootRead = null;
    }

    private void Warm(in StorageCell cell)
    {
        if (!_hasAccess) _rootRead = cell;
        else
        {
            _access.WarmUp(manager);
            _access.WarmUp(in cell);
        }
    }

    public CodeInfo GetCachedCodeInfo(Address source, bool followDelegation, IReleaseSpec spec, out Address? delegationAddress)
    {
        _pending = null;
        if (_opcode is Instruction.CALL or Instruction.STATICCALL)
        {
            Span<byte> key = stackalloc byte[64];
            key.Clear();
            source.Bytes.CopyTo(key[12..32]);
            StorageCell cell = new(manager, new UInt256(ValueKeccak.Compute(key).Bytes, isBigEndian: true));
            state.Get(in cell, out UInt256 value);
            Warm(in cell);
            Span<byte> packed = stackalloc byte[32];
            value.ToBigEndian(packed);
            if (packed[31] == 1)
            {
                _pending = new CallbackPrecompile(this, source, new Address(packed[11..31]),
                    BinaryPrimitives.ReadUInt64BigEndian(packed[3..11]));
                delegationAddress = null;
                return new CodeInfo(_pending);
            }
        }
        return inner.GetCachedCodeInfo(source, followDelegation, spec, out delegationAddress);
    }

    internal void Report(Address from, Address? to, UInt256 value, ReadOnlyMemory<byte> input, ExecutionType type, bool rejected)
    {
        if (_pending is not { } pending || type is ExecutionType.DELEGATECALL or ExecutionType.CALLCODE) return;
        _pending = null;
        if (rejected || to != pending.Proxy || _answered == claim.Callbacks.Length ||
            claim.Mode != ObservationMode.Static && type != ExecutionType.STATICCALL || !value.IsZero)
            Reject();
        RollupXObservationCallback callback = claim.Callbacks[_answered];
        if (from != callback.Source || pending.Original != callback.Target || pending.RollupId != claim.SourceRollupId ||
            !input.Span.SequenceEqual(callback.Data)) Reject();
        pending.Output = callback.Output;
        _answered++;
    }

    internal void EnsureConsumed()
    {
        if (_failed || _answered != claim.Callbacks.Length) Reject();
    }

    [DoesNotReturn]
    private void Reject()
    {
        _failed = true;
        throw new EezSettlementException("Observation callback differs from its authenticated FIFO claim.");
    }

    public IPrecompile? GetPrecompile(Address source, IReleaseSpec spec) => inner.GetPrecompile(source, spec);
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec spec) => inner.GetDelegatedCodeInfo(target, spec);
    public void InsertCode(ReadOnlyMemory<byte> code, Address owner, IReleaseSpec spec) => inner.InsertCode(code, owner, spec);
    public void InsertTransientCode(ReadOnlyMemory<byte> code, Address owner, IReleaseSpec spec) => inner.InsertTransientCode(code, owner, spec);
    public void SetDelegation(Address source, Address authority, IReleaseSpec spec) => inner.SetDelegation(source, authority, spec);
    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        inner.TryGetDelegation(address, spec, out delegatedAddress);

    private sealed class CallbackPrecompile(RollupXObservationDispatch owner, Address proxy, Address original, ulong rollupId) : IPrecompile
    {
        internal Address Proxy { get; } = proxy;
        internal Address Original { get; } = original;
        internal ulong RollupId { get; } = rollupId;
        internal byte[]? Output { get; set; }
        public bool SupportsCaching => false;
        public ulong BaseGasCost(IReleaseSpec spec) => 0;
        public ulong DataGasCost(ReadOnlyMemory<byte> data, IReleaseSpec spec) => 0;
        public Result<byte[]> Run(ReadOnlyMemory<byte> data, IReleaseSpec spec)
        {
            if (Output is null) owner.Reject();
            return Result<byte[]>.Success(Output);
        }
    }
}
