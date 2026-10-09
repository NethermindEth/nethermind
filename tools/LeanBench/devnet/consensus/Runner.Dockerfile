ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-resolute@sha256:988ebbaf9517b14dffb874611a17f6a9bb28ef1fb7663be8b5f6772cf103c164
FROM ${BASE_IMAGE}
ENV LEANVM_NUM_THREADS=1
COPY client/ /nethermind/
WORKDIR /nethermind
ENTRYPOINT ["/nethermind/nethermind"]
