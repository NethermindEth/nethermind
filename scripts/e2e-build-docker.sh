#!/bin/bash
docker build -t nethermind . && docker build -t nethermind-e2e -f Dockerfile.e2e .