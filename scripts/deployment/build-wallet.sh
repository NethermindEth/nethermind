#!/bin/bash

WALLET_PATH=$RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.BeamWallet
PUBLISH_PATH=bin/release/netcoreapp3.1
OUT=out

cd $WALLET_PATH

echo =======================================================
echo Publishing Nethermind BeamWallet for different platforms...
echo =======================================================
echo Nethermind Runner path: $WALLET_PATH

dotnet publish -c release -r $LINUX -p:PublishSingleFile=true -p:PublishTrimmed=true -o $OUT/$LIN_RELEASE
dotnet publish -c release -r $OSX -p:PublishSingleFile=true -p:PublishTrimmed=true -o $OUT/$OSX_RELEASE
dotnet publish -c release -r $WIN10 -p:PublishSingleFile=true -p:PublishTrimmed=true -o $OUT/$WIN_RELEASE

rm $OUT/$LIN_RELEASE/*.pdb
rm $OUT/$OSX_RELEASE/*.pdb
rm $OUT/$WIN_RELEASE/*.pdb

cp -r $OUT/$LIN_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$OSX_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$WIN_RELEASE $RELEASE_DIRECTORY

rm -rf $OUT

echo =======================================================
echo Building Nethermind BeamWallet completed
echo =======================================================