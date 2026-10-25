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
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "SpankBank.Worker.dll"]
