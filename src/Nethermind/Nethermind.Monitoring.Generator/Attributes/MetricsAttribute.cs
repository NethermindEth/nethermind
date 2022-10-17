using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Monitoring.Generator.Attributes;
public class MetricsAttribute : System.Attribute
{
    public string Description { get; set; }
    public string Property { get; set; }
    public string Type { get; set; }

    public MetricsAttribute(string type, string property, string description)
            => (Type, Property, Description) = (type, property, description);
}
