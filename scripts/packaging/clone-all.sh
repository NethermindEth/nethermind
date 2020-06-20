echo =======================================================
echo Cloning Nethermind sources
echo =======================================================

if [ "$1" != "" ]; then
    ./clone-runner.sh $1
    ./clone-launcher.sh
else
    ./clone-runner.sh
    ./clone-launcher.sh
fi

echo =======================================================
echo Cloning Nethermind sources completed
echo =======================================================
