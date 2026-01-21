#!/bin/bash
# Build Docker image for the fast execution of bflat on Nethermind.
export CURRENT_DIR="$(pwd)"
export PARENT_DIR=$(dirname "${CURRENT_DIR}")

# Copy nethermind artifacts
cp -R "$PARENT_DIR/artifacts/bin/StatelessExecution/release" nethermind

# Build target image
docker build --platform linux/amd64 -t bflat-tmp .
if [ "$?" != "0" ]; then
    echo "Docker build failed"
    exit 1
fi

# Run target bflat-tmp image with parameters provided to the current script.
time docker run --platform linux/amd64 \
    --cap-add=SYS_PTRACE \
    --security-opt seccomp=unconfined \
    --security-opt apparmor=unconfined \
    -e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
    -v ${CURRENT_DIR}:${CURRENT_DIR} -w ${CURRENT_DIR} -i bflat-tmp \
    "$@"
exit $?
