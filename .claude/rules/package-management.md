---
paths:
  - "**/*.csproj"
  - "**/Directory.Build.props"
  - "**/Directory.Packages.props"
---

# Central Package Management (CPM)

This repo uses Central Package Management. `Directory.Packages.props` has `ManagePackageVersionsCentrally=true`.

## Rules

- In `.csproj` files: `<PackageReference Include="Foo" />` — **NO Version attribute**
- In `Directory.Packages.props`: `<PackageVersion Include="Foo" Version="1.2.3" />`
- Adding `Version=` to a PackageReference **will break the build**

## Adding a new dependency

1. Add `<PackageVersion Include="NewPackage" Version="x.y.z" />` to `Directory.Packages.props`
2. Add `<PackageReference Include="NewPackage" />` to the relevant `.csproj`
