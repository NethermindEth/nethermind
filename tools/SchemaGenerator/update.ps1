git clone git@github.com:NethermindEth/core-scripts.git
dotnet run -v 0 --property WarningLevel=0 > ./core-scripts/schemas/config.json
cd core-scripts
git add .
git commit -m "Update schema"
git push
cd ..
rm -Recurse -Force ./core-scripts
