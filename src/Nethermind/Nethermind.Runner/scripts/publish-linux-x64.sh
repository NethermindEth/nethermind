#!/bin/bash
RUNTIME=linux-x64
OUT=out/$RUNTIME
ECHO === Publishing $RUNTIME package ===
dotnet publish -c Release -r $RUNTIME -o $OUT
rm -rf $OUT/out
rm -rf $OUT/native
rm $OUT/secp256k1.dll
rm $OUT/solc.dll
find $OUT -name "*.dylib" -type f -delete
ECHO === Published $RUNTIME package ===