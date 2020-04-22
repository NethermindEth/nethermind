echo =======================================================
echo Building Nethermind packages
echo =======================================================

if [ "$1" != "" ]; then
    ./clone-all.sh $1 && ./setup-packages.sh && ./build-runner.sh && ./build-cli.sh && ./build-launcher.sh && ./archive-packages.sh && ./azure-upload.sh && ./slack-poster.sh
else 
    ./clone-all.sh && ./setup-packages.sh && ./build-runner.sh && ./build-cli.sh && ./build-launcher.sh && ./archive-packages.sh && ./azure-upload.sh && ./slack-poster.sh
fi

echo =======================================================
echo Building Nethermind completed
echo =======================================================
