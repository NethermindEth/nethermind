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

    public async Task GenerateAll()
    {
        TestCase[] testCases = (TestCase[])Enum.GetValues(typeof(TestCase));
        var metadatas = new Metadata[testCases.Length];
        for (int i = 0; i < testCases.Length; ++i)
        {
            metadatas[i] = GetOne(testCases[i]);
        }
        var str = JsonConvert.SerializeObject(metadatas);

        await File.WriteAllTextAsync($"{_outputPath}/metadata.json", str);

    }

    private Metadata GetOne(TestCase testCase)
    {
        TestCaseMetadataAttribute metadataAttribute = GetTestCaseMetadata(testCase);
        var metadata = new Metadata(Enum.GetName(typeof(TestCase),testCase)!, metadataAttribute.Title, metadataAttribute.Description);
        return metadata;
    }

    public class Metadata(string name, string title, string description)
    {
        public string Name { get; set; } = name;

        public string Title { get; set; } = title;

        public string Description { get; set; } = description;

        public long[] GasUsed = BlockGasVariants.Variants;
    }

    static TestCaseMetadataAttribute GetTestCaseMetadata(TestCase testCase)
    {
        FieldInfo? fieldInfo = testCase.GetType().GetField(testCase.ToString());
        TestCaseMetadataAttribute[]? attributes = (TestCaseMetadataAttribute[])fieldInfo!.GetCustomAttributes(typeof(TestCaseMetadataAttribute), false);

        if (attributes == null || attributes.Length != 1)
            throw new ArgumentException("Incorrect amount of attributes found");

        return attributes[0];
    }
}
