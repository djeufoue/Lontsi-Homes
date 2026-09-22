FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["LontsiHomes.API/LontsiHomes.API.csproj", "LontsiHomes.API/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "LontsiHomes.API/LontsiHomes.API.csproj"

COPY . .
RUN dotnet publish "LontsiHomes.API/LontsiHomes.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "LontsiHomes.API.dll"]
