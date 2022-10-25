using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Linq.Expressions;

internal class ClockMonitor : IDisposable
{
    private Stopwatch _stopwatch = new();
    private Type _hostProject;
    private string _propertyName;

    public ClockMonitor(Type type, string propName)
    {
        _hostProject = type;
        _propertyName = propName;
        _stopwatch.Start();
    }
    public void Dispose()
    {
        _stopwatch.Stop();
        _hostProject.Assembly
            .GetType("Metrics")
            .GetProperty(_propertyName)
            .SetValue(null, _stopwatch.ElapsedMilliseconds);
    }
}
