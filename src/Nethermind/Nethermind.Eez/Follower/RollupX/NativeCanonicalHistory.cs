// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeCanonicalHistory(NativeL1Rpc source, EezChainRules rules) : IEezL1History
{
    private const int ReadConcurrency = 8;
    private readonly Lock _gate = new();
    private NativeL1HeaderChain? _ready;
    private long _generation;
    private int _preparing;

    internal Task<NativeL1Header> ReadCanonical(ulong number, CancellationToken token) => source.Read(number, token);

    internal NativeL1HeaderChain Read()
    {
        lock (_gate)
            return _ready ?? throw new L1SourceIncompleteException(0, "Canonical native L1 history is not ready.");
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _ready = null;
        }
    }

    internal async Task<NativeL1HeaderChain> Prepare(ulong firstNumber, L1Anchor through, CancellationToken token)
    {
        ulong bootstrap = rules.Schedule!.L1Reference.Number;
        if (firstNumber < bootstrap || firstNumber > through.Number || through.Number == ulong.MaxValue ||
            through.Number - firstNumber >= NativeL1HeaderChain.MaxHeaders)
            throw new L1SourceIncompleteException(firstNumber, "Canonical native L1 history exceeds the supported range.");
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _preparing, 1, 0) != 0)
            throw new InvalidOperationException("Native history preparation must be serialized by the follower.");
        try
        {
            return await PrepareCore(firstNumber, through, token);
        }
        catch (EezSettlementException exception)
        {
            Invalidate();
            throw new L1SourceIncompleteException(firstNumber, $"L1 returned contradictory canonical history: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _preparing, 0);
        }
    }

    private async Task<NativeL1HeaderChain> PrepareCore(ulong firstNumber, L1Anchor through, CancellationToken token)
    {
        NativeL1HeaderChain? previous;
        long generation;
        lock (_gate) (previous, generation) = (_ready, _generation);
        NativeL1Header genesis = await source.Read(0, token);
        if (genesis.Hash != source.Rules.GenesisHash || source.Rules.Specs.ChainId != rules.Schedule!.L1Reference.ChainId)
            throw NativeL1Header.Invalid("configured L1 differs from the canonical source");
        NativeL1Header first = await source.Read(firstNumber, token);
        NativeL1HeaderChain prepared = new();
        if (previous is not null && previous.FirstNumber <= firstNumber && previous.Endpoint.Number >= firstNumber &&
            previous.At(firstNumber).Hash == first.Hash)
        {
            ulong last = Math.Min(previous.Endpoint.Number, through.Number);
            NativeL1Header boundary = await source.Read(last, token);
            if (boundary.Hash == previous.At(last).Hash)
            {
                ulong keepFrom = through.Number - previous.FirstNumber < NativeL1HeaderChain.MaxHeaders ? previous.FirstNumber : firstNumber;
                prepared = previous.Range(keepFrom, last);
            }
            else throw Reorganized(through.Number);
        }
        else if (previous is not null && firstNumber <= previous.Endpoint.Number) throw Reorganized(through.Number);
        if (prepared.Count == 0) prepared.AppendChecked([first]);
        for (ulong next = prepared.Endpoint.Number + 1; next <= through.Number;)
        {
            int count = checked((int)Math.Min((ulong)ReadConcurrency, through.Number - next + 1));
            Task<NativeL1Header>[] reads = new Task<NativeL1Header>[count];
            for (int index = 0; index < count; index++) reads[index] = source.Read(next + (ulong)index, token);
            NativeL1Header[] headers = await Task.WhenAll(reads);
            prepared.AppendChecked(headers);
            next += (ulong)count;
        }
        if (prepared.Endpoint.Number != through.Number || prepared.Endpoint.Hash != through.Hash.ValueHash256 ||
            prepared.Endpoint.Timestamp != through.Timestamp || prepared.Endpoint.PrevRandao != through.MixHash.ValueHash256)
            throw NativeL1Header.Invalid("canonical history differs from the settlement anchor");
        NativeL1Header canonical = await source.Read(through.Number, token);
        if (canonical.Hash != prepared.Endpoint.Hash)
        {
            Invalidate();
            throw new L1SourceIncompleteException(through.Number, "L1 reorganized during native history preparation.");
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_generation != generation)
                throw new L1SourceIncompleteException(through.Number, "Canonical native L1 history was invalidated during preparation.");
            bool contained = previous is not null && previous.FirstNumber <= prepared.FirstNumber &&
                previous.Endpoint.Number >= prepared.Endpoint.Number && previous.At(prepared.Endpoint.Number).Hash == prepared.Endpoint.Hash;
            _ready = contained ? previous : prepared;
        }
        return prepared;
    }
    private L1SourceIncompleteException Reorganized(ulong number)
    {
        Invalidate();
        return new L1SourceIncompleteException(number, "L1 reorganized while reusing native history.");
    }

}
