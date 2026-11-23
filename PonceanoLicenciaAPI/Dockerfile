FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PonceanoLicenciaAPI.csproj ./
RUN dotnet restore PonceanoLicenciaAPI.csproj

COPY . ./
RUN dotnet publish PonceanoLicenciaAPI.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY railway-postgres-root.crt /usr/local/share/ca-certificates/railway-postgres-root.crt
RUN update-ca-certificates
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "PonceanoLicenciaAPI.dll"]
