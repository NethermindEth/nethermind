#!/bin/bash
RUNTIME=win-x64
OUT=out/$RUNTIME
ECHO === Publishing $RUNTIME package ===
dotnet publish -c Release -r $RUNTIME -o $OUT
rm -rf $OUT/out
rm -rf $OUT/native
find $OUT -name "*.so" -type f -delete
find $OUT -name "*.dylib" -type f -delete
ECHO === Published $RUNTIME package ===ls
