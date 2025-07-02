# JSON Schema Generator for Nethermind Configs

Generates schema for all the configs found. Based on NJsonSchema which has better support for enums

To update the schema, run `./update.ps1` script. For preview:

```
dotnet run -v 0 --property WarningLevel=0 > schema.json
```

For a new config to appear in the schema, project that contains the config should be included in `SchemaGenerator` solution and in `SchemaGenerator.csproj` as a `ProjectReference`.
