#! /bin/bash
cd src/Nethermind
echo 'Creating classlib'
dotnet new classlib -n $1
echo '==> Adding Nethermind API Reference'
dotnet add $1/$1.csproj reference Nethermind.Api/Nethermind.Api.csproj
echo '==> Creating a Plugin Skeleton'
wget https://gist.githubusercontent.com/1swaraj/fce0d5d74fb7e23c1c547f663e0a7508/raw/a016b87f37a8c63c6bc086ee356bde3eb79f2ff9/Plugin.cs -O $1/$2.cs
sed -i '' 's/NMPlugin/'${1}'/g' $1/$2.cs
sed -i '' 's/PluginClass/'${2}'/g' $1/$2.cs
echo '==> Building the Plugin'
cd $1
dotnet build
echo '==> Building the Runner'
cd ../Nethermind.Runner/
dotnet build
cd ..
cp -v $1/bin/Debug/net5.0/$1.dll Nethermind.Runner/bin/Debug/net5.0/plugins
echo -n "==> Do you want to start the Runner (y/n) ?"
read choice
if [ "$choice" != "${choice#[Yy]}" ] ;then
    cd Nethermind.Runner/
    dotnet run
else
    echo '==> To execute cd to Nethermind.Runner and then execute "dotnet run"'
    exit
fi