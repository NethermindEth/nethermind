# SSZ Generator Project

This project is used to generate data structures and methods that the developers want at build time. 

This Project consist of three parts:
- MainApp is a demo project of how you can mark your classes, fields and structs with SszAttribute.
- SszAttribute is a library that you need to import into your project to mark classes, fields and structs.
- SSZGenerator is a generator that will generate Simple Serialized Encoding function for marked fields and structs.


# Get Demo Running 
Head to MainApp and run
```
dotnet build

dotnet run
```

Then go to nethermind/src/Nethermind/artifacts/obj/MainApp/debug/generated, you will see the generated files there.

# For Project Setup

Add these two to Nethermind's Directory.Packages.props
```
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.10.0" />
<PackageVersion Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" />
```

In your project, added references to SSZ Generator, SSZ Attribute and Nethermind.Serialization.Ssz:
```
  <ItemGroup>
    <ProjectReference Include="..\SSZGenerator\SSZGenerator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false"/>
    <ProjectReference Include="..\..\Nethermind.Serialization.Ssz\Nethermind.Serialization.Ssz.csproj"/>
    <ProjectReference Include="..\SszAttribute\SszAttribute.csproj" />
  </ItemGroup>
```

Import
```
using SSZAttribute;
```

Sample Demo
```
using SSZAttribute;

namespace Program{
    [SSZClass]
    public partial class Product(){

        [SSZField]
        public int ProductId = 0;

        [SSZStruct]
        public struct ProductInfo
        {
            public string ProductName {get;set;}
            public ulong ProductPrice {get;set;}
        }

        [SSZFunction]
        public static void Gen()
        {
        }

    }
}
```