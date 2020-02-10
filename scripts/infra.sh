if [ "$1" != "" ]; then
    CONFIG=$(echo "$1" | tr '[:upper:]' '[:lower:]')
    cp ~/nethermind_$CONFIG/keystore/node.key.plain $CONFIG.key
    rm -r ~/nethermind_$CONFIG
    cp -r ~/nethermind ~/nethermind_$CONFIG
    cd ~/nethermind_$CONFIG
    cp ~/NLog.$CONFIG.config ~/nethermind_$CONFIG/NLog.config
    mkdir ~/nethermind_$CONFIG/keystore
    cp ~/$CONFIG.key ~/nethermind_$CONFIG/keystore/node.key.plain
    dotnet Nethermind.Runner.dll --config ../$CONFIG.cfg
else
    cp ~/nethermind_mainnet/keystore/node.key.plain mainnet.key
    rm -r ~/nethermind_mainnet
    cp -r ~/nethermind ~/nethermind_mainnet
    cd ~/nethermind_mainnet
    cp ~/NLog.mainnet.config ~/nethermind_mainnet/NLog.config
    mkdir ~/nethermind_mainnet/keystore
    cp ~/mainnet.key ~/nethermind_mainnet/keystore/node.key.plain
    dotnet Nethermind.Runner.dll --config ../mainnet.cfg
fi