FROM microsoft/dotnet-nightly:2.1-sdk AS build-env
WORKDIR /app

# copy csproj and restore as distinct layers
COPY *.csproj ./
COPY NuGet.config ./
RUN dotnet restore

# copy everything else and build
COPY . ./
RUN dotnet publish -c Release -o out --no-restore


# build runtime image
FROM microsoft/dotnet-nightly:2.1-runtime-alpine

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT false
RUN apk add --no-cache icu-libs

ENV LC_ALL en_US.UTF-8
ENV LANG en_US.UTF-8

WORKDIR /app
COPY --from=build-env /app/out ./
ENTRYPOINT ["dotnet", "dotnetapp.dll"]
