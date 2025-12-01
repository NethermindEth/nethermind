using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using HdrHistogram;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Nethermind.Paprika.Importer;

interface IMeasurement : IRenderable
{
    void Update(double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags);
}

abstract class Measurement : JustInTimeRenderable, IMeasurement
{
    private readonly Instrument _instrument;
    private const long NoValue = Int64.MaxValue;

    private long _value;

    private Measurement(Instrument instrument)
    {
        _instrument = instrument;
    }

    protected override IRenderable Build()
    {
        var value = Volatile.Read(ref _value);
        return value == NoValue ? new Text("") : new Text(value.ToString());
    }

    public void Update(double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var updated = Update(measurement);
        var previous = Interlocked.Exchange(ref _value, updated);

        if (updated != previous)
        {
            MarkAsDirty();
        }
    }

    protected abstract long Update(double measurement);

    public override string ToString() => $"{nameof(Instrument)}: {_instrument.Name}, Value: {Volatile.Read(ref _value)}";

    public static IMeasurement Build(Instrument instrument)
    {
        var type = instrument.GetType();
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();

            if (definition == typeof(ObservableGauge<>))
            {
                return new GaugeMeasurement(instrument);
            }

            if (definition == typeof(Counter<>))
            {
                return new CounterMeasurement(instrument);
            }

            if (definition == typeof(Histogram<>))
            {
                return new HistogramHdrMeasurement(instrument);
            }
        }

        throw new NotImplementedException($"Not implemented for type {type}");
    }

    private class GaugeMeasurement(Instrument instrument) : Measurement(instrument)
    {
        protected override long Update(double measurement) => (long)measurement;
    }

    private class HistogramLastMeasurement(Instrument instrument) : Measurement(instrument)
    {
        protected override long Update(double measurement)
        {
            return (long)measurement;
        }
    }

    private class HistogramHdrMeasurement(Instrument instrument) : JustInTimeRenderable, IMeasurement
    {
        private readonly ConcurrentQueue<long> _measurements = new();
        private readonly LongHistogram _histogram = new(1, 1, int.MaxValue, 4);

        protected override IRenderable Build()
        {
            // dequeue all first
            while (_measurements.TryDequeue(out var measurement))
            {
                if (measurement > 0)
                {
                    _histogram.RecordValue(measurement);
                }
            }

            try
            {
                var p50 = _histogram.GetValueAtPercentile(50).ToString().PadLeft(5);
                var p90 = _histogram.GetValueAtPercentile(90).ToString().PadLeft(5);
                var p99 = _histogram.GetValueAtPercentile(99).ToString().PadLeft(5);

                return new Markup($"[green]{p50}[/] |[yellow]{p90}[/] |[red]{p99}[/]");
            }
            catch
            {
                return new Text(" N/A yet");
            }
        }

        public void Update(double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            _measurements.Enqueue((long)measurement);
            MarkAsDirty();
        }

        public override string ToString() => $"{nameof(Instrument)}: {instrument.Name}, Histogram";
    }

    private class CounterMeasurement(Instrument instrument) : Measurement(instrument)
    {
        private long _sum;

        protected override long Update(double measurement) => Interlocked.Add(ref _sum, (long)measurement);
    }
}
