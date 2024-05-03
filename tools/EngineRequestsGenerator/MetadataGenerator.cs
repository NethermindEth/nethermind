// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace EngineRequestsGenerator;

public class MetadataGenerator
{
    private readonly string _outputPath;

    public MetadataGenerator(string outputPath)
    {
        _outputPath = outputPath;
    }
    public async Task Generate(TestCase testCase)
    {
        TestCaseMetadataAttribute metadataAttribute = GetTestCaseMetadata(testCase);
        var metadata = new Metadata(metadataAttribute.Name, metadataAttribute.Description);
        var str = JsonConvert.SerializeObject(metadata);

        await File.WriteAllTextAsync($"{_outputPath}/{testCase}.metadata", str);
    }

    public class Metadata(string name, string description)
    {
        public string Name { get; set; } = name;

        public string Description { get; set; } = description;
    }

    static TestCaseMetadataAttribute GetTestCaseMetadata(TestCase testCase)
    {
        FieldInfo? fieldInfo = testCase.GetType().GetField(testCase.ToString());
        TestCaseMetadataAttribute[]? attributes = (TestCaseMetadataAttribute[])fieldInfo?.GetCustomAttributes(typeof(TestCaseMetadataAttribute), false);

        if (attributes == null || attributes.Length != 1)
            throw new ArgumentException("Incorrect amount of attributes found");

        return attributes[0];
    }
}
