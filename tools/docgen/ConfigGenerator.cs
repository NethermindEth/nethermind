// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using Nethermind.Config;

namespace Nethermind.DocGen;

internal static class ConfigGenerator
{
    internal static void Generate()
    {
        var startMark = "<!--[start autogen]-->";
        var endMark = "<!--[end autogen]-->";
        var fileName = "configuration.md";
        var excluded = Enumerable.Empty<string>();

        var types = Directory
            .GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
            .SelectMany(a => Assembly.LoadFrom(a).GetExportedTypes())
            .Where(t => t.IsInterface && typeof(IConfig).IsAssignableFrom(t) &&
                !excluded.Any(x => t.FullName?.Contains(x, StringComparison.Ordinal) ?? false))
            .OrderBy(t => t.Name);
        
        File.Delete($"~{fileName}");

        using var readStream = new StreamReader(File.OpenRead(fileName));
        using var writeStream = new StreamWriter(File.OpenWrite($"~{fileName}"));

        writeStream.NewLine = "\n";

        var line = string.Empty;

        do
        {
            line = readStream.ReadLine();

            writeStream.WriteLine(line);
        }
        while (!line?.Equals(startMark, StringComparison.Ordinal) ?? false);

        writeStream.WriteLine();

        foreach (var type in types)
            WriteMarkdown(writeStream, type);

        var skip = true;

        for (line = readStream.ReadLine(); line is not null; line = readStream.ReadLine())
        {
            if (skip)
            {
                if (line?.Equals(endMark, StringComparison.Ordinal) ?? false)
                    skip = false;
                else
                    continue;
            }

            writeStream.WriteLine(line);
        }

        readStream.Close();
        writeStream.Close();

        File.Move($"~{fileName}", fileName, true);

        Console.WriteLine($"Updated {fileName}");
    }

    private static void WriteMarkdown(StreamWriter file, Type configType)
    {
        var categoryAttr = configType.GetCustomAttribute<ConfigCategoryAttribute>();

        if (categoryAttr?.HiddenFromDocs ?? false)
            return;

        var props = configType.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name);

        if (!props.Any())
            return;

        var moduleName = configType.Name[1..].Replace("Config", null);

        file.WriteLine($"""
            <details>
            <summary className="nd-details-heading">

            #### {moduleName}

            </summary>
            <p>

            """);

        foreach (var prop in props)
        {
            var itemAttr = prop.GetCustomAttribute<ConfigItemAttribute>();

            if (itemAttr?.HiddenFromDocs ?? true)
                continue;

            var description = itemAttr.Description.Replace("\n", "\n  ").TrimEnd(' ');

            file.Write($"""
                - **`--{moduleName}.{prop.Name} <value>`** `NETHERMIND_{moduleName.ToUpperInvariant()}CONFIG_{prop.Name.ToUpperInvariant()}`

                  {description}
                """);

            var startsFromNewLine = WriteAllowedValues(file, prop.PropertyType) || description.EndsWith('\n');

            WriteDefaultValue(file, itemAttr, startsFromNewLine);

            file.WriteLine();
            file.WriteLine();
        }

        file.WriteLine("""
            </p>
            </details>

            """);
    }

    private static bool WriteAllowedValues(StreamWriter file, Type type)
    {
        if (type == typeof(bool))
        {
            file.Write(" Allowed values: `true` `false`.");

            return false;
        }

        if (type.IsEnum)
        {
            file.WriteLine("""


                  Allowed values:

                """);

            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public);

            foreach (var field in fields)
            {
                var attr = field.GetCustomAttribute<DescriptionAttribute>();
                var description = string.IsNullOrEmpty(attr?.Description) ? null : $": {attr.Description}";

                file.WriteLine($"    - `{field.Name}{description}`");
            }

            file.WriteLine();

            return true;
        }

        return false;
    }

    private static void WriteDefaultValue(StreamWriter file, ConfigItemAttribute attr, bool indentAsNewLine)
    {
        if (string.IsNullOrEmpty(attr.DefaultValue))
            return;

        if (attr.DefaultValue.Contains('\n'))
            file.WriteLine($"""

                  Defaults to:

                  {attr.DefaultValue.Replace("\n", "\n  ")}
                """);
        else
            file.Write($"{(indentAsNewLine ? "  " : " ")}Defaults to `{attr.DefaultValue}`.");
    }
}
