# The image of the operator: built with the .NET 10 SDK, run on the chiseled runtime, which has no shell and runs as a non-root user.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ChromaDotNet.Operator/ src/ChromaDotNet.Operator/
RUN dotnet publish src/ChromaDotNet.Operator/ChromaDotNet.Operator.csproj -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "ChromaDotNet.Operator.dll"]
