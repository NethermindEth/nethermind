// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using Nethermind.Config;
using Spectre.Console;

namespace Nethermind.DocGen;

internal static class ConfigGenerator
{
    internal static void Generate(string path)
    {
        path = Path.Join(path, "docs", "fundamentals");

        var startMark = "<!--[start autogen]-->";
        var endMark = "<!--[end autogen]-->";
        var excluded = Enumerable.Empty<string>();
        var types = Directory
            .GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
            .SelectMany(a => Assembly.LoadFrom(a).GetExportedTypes())
            .Where(t => t.IsInterface && typeof(IConfig).IsAssignableFrom(t) &&
                !excluded.Any(x => t.FullName?.Contains(x, StringComparison.Ordinal) ?? false))
            .OrderBy(t => t.Name);
        var fileName = Path.Join(path, "configuration.md");
        var tempFileName = Path.Join(path, "~configuration.md");

        // Delete the temp file if it exists
        File.Delete(tempFileName);

        using var readStream = new StreamReader(File.OpenRead(fileName));
        using var writeStream = new StreamWriter(File.OpenWrite(tempFileName));

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

        File.Move(tempFileName, fileName, true);
        File.Delete(tempFileName);

        AnsiConsole.MarkupLine($"[green]Updated[/] {fileName}");
    }

    private static void WriteMarkdown(StreamWriter file, Type configType)
    {
        var categoryAttr = configType.GetCustomAttribute<ConfigCategoryAttribute>();

        if (categoryAttr?.HiddenFromDocs ?? false)
            return;

        var props = configType.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(p => p.Name);

        if (!props.Any())
            return;

        static (string, string) GetValue(PropertyInfo prop) =>
            prop.PropertyType == typeof(bool) ? ("true|false", "[true|false]") : ("<value>", "<value>");

        var moduleName = configType.Name[1..].Replace("Config", null);

        file.WriteLine($"""
            ### {moduleName}

            """);

        foreach (var prop in props)
        {
            var itemAttr = prop.GetCustomAttribute<ConfigItemAttribute>();

            if (itemAttr?.HiddenFromDocs ?? true)
                continue;

            var description = itemAttr.Description.Replace("\n", "\n  ").TrimEnd(' ');
            (string value, string cliValue) = GetValue(prop);

            file.Write($$"""
                - #### `{{moduleName}}.{{prop.Name}}` \{#{{moduleName.ToLowerInvariant()}}-{{prop.Name.ToLowerInvariant()}}\}

                  <Tabs groupId="usage">
                  <TabItem value="cli" label="CLI">
                  ```
                  --{{moduleName.ToLowerInvariant()}}-{{prop.Name.ToLowerInvariant()}} {{cliValue}}
                  --{{moduleName}}.{{prop.Name}} {{cliValue}}
                  ```
                  </TabItem>
                  <TabItem value="env" label="Environment variable">
                  ```
                  NETHERMIND_{{moduleName.ToUpperInvariant()}}CONFIG_{{prop.Name.ToUpperInvariant()}}={{value}}
                  ```
                  </TabItem>
                  <TabItem value="config" label="Configuration file">
                  ```json
                  {
                    "{{moduleName}}": {
                      "{{prop.Name}}": {{value}}
                    }
                  }
                  ```
                  </TabItem>
                  </Tabs>

                  {{description}}
                """);

            var startsFromNewLine = WriteAllowedValues(file, prop.PropertyType) || description.EndsWith('\n');

            WriteDefaultValue(file, itemAttr, startsFromNewLine);

            file.WriteLine();
            file.WriteLine();
        }

        file.WriteLine();
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

                file.WriteLine($"    - `{field.Name}`{description}");
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
