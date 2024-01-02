// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics;
using App.Metrics.ReservoirSampling;
using App.Metrics.ReservoirSampling.Uniform;

namespace Nethermind.Tools.Kute.Extensions;

public class CompleteReservoir : IReservoir
{
    private const int DefaultSize = 10_000;

    private readonly List<UserValueWrapper> _values;

    public CompleteReservoir() : this(DefaultSize) { }

    public CompleteReservoir(int size)
    {
        _values = new List<UserValueWrapper>(size);
    }

    public IReservoirSnapshot GetSnapshot(bool resetReservoir)
    {
        long count = _values.Count;
        double sum = _values.Sum(v => v.Value);
        IEnumerable<long> values = _values.Select(v => v.Value);

        if (resetReservoir)
        {
            _values.Clear();
        }

        return new UniformSnapshot(count, sum, values);
    }

    public IReservoirSnapshot GetSnapshot() => GetSnapshot(false);

    public void Reset() => _values.Clear();

    public void Update(long value, string userValue) => _values.Add(new UserValueWrapper(value, userValue));

    public void Update(long value) => _values.Add(new UserValueWrapper(value));

}
