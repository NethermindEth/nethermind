# .NET Core Docker Alpine Production Sample (Preview)

This .NET Core Docker sample demonstrates a best practice pattern for building Alpine based Docker images for .NET Core apps for production.

The primary goal of Alpine is very small deployments.  Images can be pulled quicker and will have a smaller attack surface area.  The .NET Core Alpine Docker images are currently in preview. See the [.NET Core Alpine Docker Image announcement](https://github.com/dotnet/dotnet-docker-nightly/issues/500) for additional details.

The [sample Dockerfile](Dockerfile) creates a .NET Core application Docker image based off of the [.NET Core Runtime Alpine Preview Docker image](https://hub.docker.com/r/microsoft/dotnet-nightly/).

It uses the [Docker multi-stage build feature](https://github.com/dotnet/announcements/issues/18) to build the sample in a container based on the larger [.NET Core SDK Docker image](https://hub.docker.com/r/microsoft/dotnet/) and then copies the final build result into a Docker image based on the smaller [.NET Core Docker Runtime image](https://hub.docker.com/r/microsoft/dotnet/). The SDK image contains tools that are required to build applications while the runtime image does not.

This sample requires [Docker 17.06](https://docs.docker.com/release-notes/docker-ce) or later of the [Docker client](https://www.docker.com/products/docker). You need the latest Windows 10 or Windows Server 2016 to use [Windows containers](http://aka.ms/windowscontainers). The instructions assume you have the [Git](https://git-scm.com/downloads) client installed.

## Getting the sample

The easiest way to get the sample is by cloning the samples repository with git, using the following instructions.

```console
git clone https://github.com/dotnet/dotnet-docker-samples/
```

You can also [download the repository as a zip](https://github.com/dotnet/dotnet-docker-samples/archive/master.zip).

## Build and run the sample with Docker

You can build and run the sample in Docker using the following commands. The instructions assume that you are in the root of the repository.

```console
cd dotnetapp-prod-alpine-preview
docker build -t dotnetapp-prod-alpine-preview .
docker run --rm dotnetapp-prod-alpine-preview Hello .NET Core from Docker
```

Note: The instructions above work only with Linux containers.

## Build and run the sample without the Globalization Invariant Mode

The Alpine based .NET Core Runtime Docker image has the [.NET Core 2.0 Globalization Invariant Mode](https://github.com/dotnet/announcements/issues/20) enabled in order to reduce the default size of the image.  Use cases that cannot tolerate Globalization Invariant Mode can reset the `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` environment variable and install the required ICU package.  The [Globalization Dockerfile](Dockerfile.globalization) illustrates how this can be done.

You can build and run the sample in Docker using the following commands. The instructions assume that you are in the root of the repository.

```console
cd dotnetapp-prod-alpine-preview
docker build -t dotnetapp-prod-alpine-preview -f Dockerfile.globalization .
docker run --rm dotnetapp-prod-alpine-preview Hello .NET Core from Docker
```

Note: The instructions above work only with Linux containers.

## Docker Images used in this sample

The following Docker images are used in this sample

* [microsoft/dotnet-nightly:2.1-sdk](https://hub.docker.com/r/microsoft/dotnet-nightly)
* [microsoft/dotnet-nightly:2.1-runtime-alpine](https://hub.docker.com/r/microsoft/dotnet-nightly)

## Related Resources

* [ASP.NET Core Production Docker sample](../aspnetapp/README.md)
* [.NET Core Docker samples](../README.md)
* [.NET Framework Docker samples](https://github.com/Microsoft/dotnet-framework-docker-samples)
