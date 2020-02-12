cd ~/src/nethermind
git stash
git pull
git stash apply
cd ../..
cd ~/src/nethermind/src/Nethermind/Nethermind.Runner
dotnet build -c Release -o ~/nethermind
cp ~/NLog.config ~/nethermind/NLog.config