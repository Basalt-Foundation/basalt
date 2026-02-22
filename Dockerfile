# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files for restore
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props Basalt.sln ./
COPY src/core/Basalt.Core/Basalt.Core.csproj src/core/Basalt.Core/
COPY src/core/Basalt.Crypto/Basalt.Crypto.csproj src/core/Basalt.Crypto/
COPY src/core/Basalt.Codec/Basalt.Codec.csproj src/core/Basalt.Codec/
COPY src/storage/Basalt.Storage/Basalt.Storage.csproj src/storage/Basalt.Storage/
COPY src/network/Basalt.Network/Basalt.Network.csproj src/network/Basalt.Network/
COPY src/consensus/Basalt.Consensus/Basalt.Consensus.csproj src/consensus/Basalt.Consensus/
COPY src/execution/Basalt.Execution/Basalt.Execution.csproj src/execution/Basalt.Execution/
COPY src/api/Basalt.Api.Grpc/Basalt.Api.Grpc.csproj src/api/Basalt.Api.Grpc/
COPY src/api/Basalt.Api.Rest/Basalt.Api.Rest.csproj src/api/Basalt.Api.Rest/
COPY src/api/Basalt.Api.GraphQL/Basalt.Api.GraphQL.csproj src/api/Basalt.Api.GraphQL/
COPY src/compliance/Basalt.Compliance/Basalt.Compliance.csproj src/compliance/Basalt.Compliance/
COPY src/confidentiality/Basalt.Confidentiality/Basalt.Confidentiality.csproj src/confidentiality/Basalt.Confidentiality/
COPY src/sdk/Basalt.Sdk.Contracts/Basalt.Sdk.Contracts.csproj src/sdk/Basalt.Sdk.Contracts/
COPY src/sdk/Basalt.Sdk.Analyzers/Basalt.Sdk.Analyzers.csproj src/sdk/Basalt.Sdk.Analyzers/
COPY src/sdk/Basalt.Sdk.Testing/Basalt.Sdk.Testing.csproj src/sdk/Basalt.Sdk.Testing/
COPY src/generators/Basalt.Generators.Codec/Basalt.Generators.Codec.csproj src/generators/Basalt.Generators.Codec/
COPY src/generators/Basalt.Generators.Json/Basalt.Generators.Json.csproj src/generators/Basalt.Generators.Json/
COPY src/generators/Basalt.Generators.Contracts/Basalt.Generators.Contracts.csproj src/generators/Basalt.Generators.Contracts/
COPY src/node/Basalt.Node/Basalt.Node.csproj src/node/Basalt.Node/
COPY tools/Basalt.Cli/Basalt.Cli.csproj tools/Basalt.Cli/

# Copy all source code upfront (restore + publish in one step for AOT consistency)
COPY src/ src/
COPY tools/ tools/

# Publish the node (release, framework-dependent — AOT disabled for containers)
RUN dotnet publish src/node/Basalt.Node/Basalt.Node.csproj \
    -c Release \
    -o /app \
    -p:PublishAot=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Install RocksDB native library (NuGet package has ARM64 stub only)
RUN apt-get update && apt-get install -y --no-install-recommends librocksdb-dev curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# LOW-N02: Run as non-root user for defense-in-depth
RUN adduser --disabled-password --gecos "" --home /data basalt && \
    mkdir -p /data/basalt && chown -R basalt:basalt /data
USER basalt

# Expose ports: REST API (5000), gRPC (5001), P2P (30303)
EXPOSE 5000 5001 30303

ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

ENTRYPOINT ["dotnet", "Basalt.Node.dll"]
