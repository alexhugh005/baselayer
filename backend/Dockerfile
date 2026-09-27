FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/BaseLayer.Api/BaseLayer.Api.csproj src/BaseLayer.Api/
COPY src/BaseLayer.Application/BaseLayer.Application.csproj src/BaseLayer.Application/
COPY src/BaseLayer.Data/BaseLayer.Data.csproj src/BaseLayer.Data/
COPY src/BaseLayer.Domain/BaseLayer.Domain.csproj src/BaseLayer.Domain/
RUN dotnet restore src/BaseLayer.Api/BaseLayer.Api.csproj
COPY src/ src/
RUN dotnet publish src/BaseLayer.Api/BaseLayer.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /app/data && chown app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaseLayer.Api.dll"]
