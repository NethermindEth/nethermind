# SSZ Generator Project

This project is used to generate data structures and methods that the developers want at build time. 
Mark your classes, methods, or fields with Attributes provided, head to MainApp and run
```
dotnet build

dotnet run
```

Then go to nethermind/src/Nethermind/artifacts/obj/MainApp/debug/generated, you will see the generated files there.



add these two to Directory.Packages.props
```
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.10.0" />
<PackageVersion Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" />
```