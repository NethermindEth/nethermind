using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace Nethermind.Paprika.Importer;

public class MetricsReporter : IDisposable
{
    private readonly object _sync = new();
    private readonly MeterListener _listener;
    private readonly Dictionary<Meter, Dictionary<Instrument, IMeasurement>> _instrument2State = new();

    public MetricsReporter()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ShouldReport(instrument) == false)
                    return;

                lock (_sync)
                {
                    var meter = instrument.Meter;
                    ref var dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_instrument2State, meter,
                        out var exists);

                    if (!exists)
                    {
                        dict = new Dictionary<Instrument, IMeasurement>();
                    }

                    var state = Measurement.Build(instrument);
                    dict!.Add(instrument, state);

                    listener.EnableMeasurementEvents(instrument, state);
                }
            },
            MeasurementsCompleted = (instrument, cookie) =>
            {
                if (ShouldReport(instrument) == false)
                    return;

                lock (_sync)
                {
                    var instruments = _instrument2State[instrument.Meter];
                    instruments.Remove(instrument, out _);
                    if (instruments.Count == 0)
                        _instrument2State.Remove(instrument.Meter);
                }
            }
        };

        _listener.Start();

        _listener.SetMeasurementEventCallback<double>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<float>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<long>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<int>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<short>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<byte>((i, m, l, c) => ((IMeasurement)c!).Update(m, l));
        _listener.SetMeasurementEventCallback<decimal>((i, m, l, c) => ((IMeasurement)c!).Update((double)m, l));
    }

    private static bool ShouldReport(Instrument instrument) => instrument.Meter.Name.Contains("Paprika");

    public void Observe()
    {
        _listener.RecordObservableInstruments();
    }

    public void Dispose()
    {
        _listener.Dispose();
    }
}
