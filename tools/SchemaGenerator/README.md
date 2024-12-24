# JSON schema generator for nethermind configs

Generates schema for all the configs found. Based on NJsonSchema which has better support for enums

To update the schema:

```
dotnet build .
dotnet run -v 0 --property WarningLevel=0 > ../../src/Nethermind/Nethermind.Runner/configs/schema.json
```
