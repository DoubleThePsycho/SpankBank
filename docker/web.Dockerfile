FROM node:22-alpine AS frontend
WORKDIR /repo/src/Web/Frontend

# Install first so this layer is cached until package files change.
COPY src/Web/Frontend/package.json src/Web/Frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/Web/Frontend/ ./
# Vite writes to ../wwwroot/dist
RUN npx vite build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY SpankBank.slnx ./
COPY src/Core/SpankBank.Core.csproj src/Core/
COPY src/Data/SpankBank.Data.csproj src/Data/
COPY src/Web/SpankBank.Web.csproj src/Web/
RUN dotnet restore src/Web/SpankBank.Web.csproj

COPY src/ src/
COPY --from=frontend /repo/src/Web/wwwroot/dist src/Web/wwwroot/dist
RUN dotnet publish src/Web/SpankBank.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SpankBank.Web.dll"]
