FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PonceanoLicenciaAPI.csproj ./
RUN dotnet restore PonceanoLicenciaAPI.csproj

COPY . ./
RUN dotnet publish PonceanoLicenciaAPI.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "PonceanoLicenciaAPI.dll"]
