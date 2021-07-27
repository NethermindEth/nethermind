#! /bin/bash
cd src/Nethermind
echo 'Creating classlib'
dotnet new classlib -n $1
echo '==> Adding Nethermind API Reference'
dotnet add $1/$1.csproj reference Nethermind.Api/Nethermind.Api.csproj
echo '==> Creating a Plugin Skeleton'
wget https://gist.githubusercontent.com/1swaraj/fce0d5d74fb7e23c1c547f663e0a7508/raw/c6d12a4f2e86b00e27231b6eaccd158e769a42e1/Plugin.cs -O $1/$2.cs
wget https://gist.githubusercontent.com/1swaraj/4305e77394f081ad322cbdec6767c42b/raw/d69792c85cbf605d594e32b3ad736ea74837cda9/Plugin.csproj -O $1/$1.csproj
sed -i '' 's/NMPlugin/'${1}'/g' $1/$2.cs
sed -i '' 's/PluginClass/'${2}'/g' $1/$2.cs
sed -i '' 's/PLUGINNAME/'${1}'/g' $1/$1.csproj
echo '==> Building the Plugin'
cd $1
rm -rf Class1.cs
dotnet build
cd ..
cp -v $1/bin/Debug/net5.0/$1.dll Nethermind.Runner/bin/Debug/net5.0/plugins
echo "==> Your plugin is initialized"
echo "==> To execute"
echo "cd src/Nethermind/Nethermind.Runner"
echo "dotnet run"