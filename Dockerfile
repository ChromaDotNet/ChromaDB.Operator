# The image of the operator: built with the .NET 10 SDK, run on the chiseled ASP.NET Core runtime (the probes answer over HTTP), which
# has no shell and runs as a non-root user.
# The SDK runs on the machine of the build and publishes for the platform of the image, so that amd64 and arm64 build without emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ChromaDotNet.Operator/ src/ChromaDotNet.Operator/
RUN dotnet publish src/ChromaDotNet.Operator/ChromaDotNet.Operator.csproj -c Release -a "$TARGETARCH" -o /out --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "ChromaDotNet.Operator.dll"]
