FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Oficina.Api/Oficina.Api.csproj src/Oficina.Api/
COPY src/Oficina.Aplicacao/Oficina.Aplicacao.csproj src/Oficina.Aplicacao/
COPY src/Oficina.Dominio/Oficina.Dominio.csproj src/Oficina.Dominio/
COPY src/Oficina.Infraestrutura/Oficina.Infraestrutura.csproj src/Oficina.Infraestrutura/
RUN dotnet restore src/Oficina.Api/Oficina.Api.csproj
COPY src/ src/
RUN dotnet publish src/Oficina.Api/Oficina.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Oficina.Api.dll"]
