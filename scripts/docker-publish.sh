#!/bin/bash
GIT_TAG=$(git describe --tags)
DOCKER_TAGS="latest $GIT_TAG"
DOCKER_IMAGE_NAME=nethermind.runner

if [ "$TRAVIS_BRANCH" != "master" ]; then
    exit 0
fi

if [[ ! $TAG =~ ^v\d* ]] && [[ ! $TAG =~ ^stable_* ]]; then
    exit 0
fi

docker login -u $DOCKER_USERNAME -p $DOCKER_PASSWORD

for DOCKER_TAG in $DOCKER_TAGS; do
    docker build -t $DOCKER_IMAGE_NAME:$DOCKER_TAG .
    docker tag $DOCKER_IMAGE_NAME:$DOCKER_TAG $DOCKER_USERNAME/$DOCKER_IMAGE_NAME:$DOCKER_TAG
    docker push $DOCKER_USERNAME/$DOCKER_IMAGE_NAME:$DOCKER_TAG
done
