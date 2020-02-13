if [ "$1" != "" ]; then
    CONFIG=$(echo "$1" | tr '[:upper:]' '[:lower:]')
else
    CONFIG="mainnet"
fi

cp ~/nethermind_$CONFIG/keystore/node.key.plain $CONFIG.key
rm -r ~/nethermind_$CONFIG
cp -r ~/nethermind ~/nethermind_$CONFIG
cd ~/nethermind_$CONFIG
cp ~/NLog.$CONFIG.config ~/nethermind_$CONFIG/NLog.config
mkdir ~/nethermind_$CONFIG/keystore
cp ~/$CONFIG.key ~/nethermind_$CONFIG/keystore/node.key.plain
dotnet Nethermind.Runner.dll --config ../$CONFIG.cfg