// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Era1;
//See https://github.com/ethereum/portal-network-specs/blob/master/history-network.md#algorithms
internal class AccumulatorCalculator : IDisposable
{
    ArrayPoolList<ReadOnlyMemory<byte>> _roots;
    private bool _disposedValue;

    public AccumulatorCalculator()
    {
        _roots = new(EraWriter.MaxEra1Size);
    }

    public void Add(Hash256? headerHash, UInt256 td)
    {
        if (headerHash is null) throw new ArgumentNullException(nameof(headerHash));

        Merkleizer merkleizer = new Merkleizer((int)Merkle.NextPowerOfTwoExponent(2));
        merkleizer.Feed(headerHash.Bytes);
        merkleizer.Feed(td.ToLittleEndian());
        _roots.Add(merkleizer.CalculateRoot().ToLittleEndian());
    }

    public ValueHash256 ComputeRoot()
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.Feed(_roots, EraWriter.MaxEra1Size);
        return new ValueHash256(merkleizer.CalculateRoot().ToLittleEndian());
    }

    internal void Clear() => _roots.Clear();

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _roots.Dispose();
            }
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
