#!/bin/bash
dotnet publish -c release -o ./bin/docker && docker build -t nethermind.runner .