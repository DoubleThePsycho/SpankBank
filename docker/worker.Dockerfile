FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore first so this layer is cached until a project file changes.
COPY SpankBank.slnx ./
COPY src/Core/SpankBank.Core.csproj src/Core/
COPY src/Data/SpankBank.Data.csproj src/Data/
COPY src/Worker/SpankBank.Worker.csproj src/Worker/
RUN dotnet restore src/Worker/SpankBank.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Worker/SpankBank.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Flash previews: Ruffle's headless exporter renders on software Vulkan (lavapipe), so the server needs no GPU.
# The exporter binary comes from a separate image that is built once by hand (the Rust build takes a long time):
#   docker build -f docker/ruffle-exporter.Dockerfile -t spankbank-ruffle-exporter:0.7.1 .
RUN apt-get update \
    && apt-get install -y --no-install-recommends libvulkan1 mesa-vulkan-drivers libx11-6 libxext6 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=spankbank-ruffle-exporter:0.7.1 /exporter /usr/local/bin/exporter
COPY --from=spankbank-ruffle-exporter:0.7.1 /LICENSE.md /usr/share/doc/ruffle-exporter/LICENSE.md
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "SpankBank.Worker.dll"]
