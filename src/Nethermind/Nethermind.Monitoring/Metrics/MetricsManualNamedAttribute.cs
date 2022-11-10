using System;

namespace Nethermind.Monitoring.Metrics;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class MetricsManualNamedAttribute : Attribute
{
    public string Name { get; }

    /// <summary>
    /// Collects a user defined name for use in metrics registration, will be used directly as given (not nethermind_var_name_snake_case form CamelCase without nethermind)
    /// </summary>
    /// <param name="metricName">Metrics name</param>
    public MetricsManualNamedAttribute(string metricName)
    {
        Name = metricName;
    }
}
