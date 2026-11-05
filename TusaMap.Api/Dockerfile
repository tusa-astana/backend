# Build the API with the .NET 10 SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TusaMap.Api.csproj ./
RUN dotnet restore TusaMap.Api.csproj
COPY . ./
RUN dotnet publish TusaMap.Api.csproj --configuration Release --output /app/publish /p:UseAppHost=false

# Run the published API in the smaller ASP.NET runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
ENV PORT=10000
EXPOSE 10000

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "TusaMap.Api.dll"]
