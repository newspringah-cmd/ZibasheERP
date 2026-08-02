FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ZibasheERP.slnx ./
COPY ZibasheERP.API/ZibasheERP.API.csproj ZibasheERP.API/
COPY ZibasheERP.Application/ZibasheERP.Application.csproj ZibasheERP.Application/
COPY ZibasheERP.Domain/ZibasheERP.Domain.csproj ZibasheERP.Domain/
COPY ZibasheERP.Infrastructure/ZibasheERP.Infrastructure.csproj ZibasheERP.Infrastructure/
COPY ZibasheERP.Shared/ZibasheERP.Shared.csproj ZibasheERP.Shared/
RUN dotnet restore ZibasheERP.API/ZibasheERP.API.csproj -p:NuGetAudit=false

COPY ZibasheERP.API/ ZibasheERP.API/
COPY ZibasheERP.Application/ ZibasheERP.Application/
COPY ZibasheERP.Domain/ ZibasheERP.Domain/
COPY ZibasheERP.Infrastructure/ ZibasheERP.Infrastructure/
COPY ZibasheERP.Shared/ ZibasheERP.Shared/
RUN dotnet publish ZibasheERP.API/ZibasheERP.API.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ZibasheERP.API.dll"]
