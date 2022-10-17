using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Monitoring.Generator.Attributes;
[Flags] public enum MonitorType
{
    Time, Count
}
internal class MonitorAttribute : MetricsAttribute
{
    public string Description { get; set; }
    public MonitorType Hook { get; set; }

    public MonitorAttribute(string target, string description, MonitorType monitor)
            : base(
                    typeof(long).Name,
                    $"{target}{ monitor switch { MonitorType.Time => "ExecutionTime",MonitorType.Count => "ExecutionCount" }}",
                    $"{target} Execution {monitor switch { MonitorType.Time => "Duration ", MonitorType.Count => "Requests" }}")
            => (Description, Hook) = (description, monitor);
}
