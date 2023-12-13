using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace HiveCompare.Models
{
    public class HiveTestResult
    {
        public Dictionary<string, TestCase> TestCases { get; set; } = default!;
    }

    public class TestCase
    {
        public string Key => Name + Description;
        public string Name { get; set; } = default!;
        public string Description { get; set; } = default!;
        public CaseResult SummaryResult { get; set; } = default!;

        [JsonIgnore]
        public Dictionary<string, ClientInfo>? clientInfo { get; set; }

        public override string ToString() => JsonSerializer.Serialize(this, Program.SERIALIZER_OPTIONS);
    }

    public class CaseResult
    {
        public bool Pass { get; set; }

        [JsonIgnore]
        public string Details { get; set; } = default!;
    }

    public class ClientInfo
    {
        public string Id { get; set; } = default!;
        public string Ip { get; set; } = default!;
        public string Name { get; set; } = default!;
        public string logFile { get; set; } = default!;
    }
}
