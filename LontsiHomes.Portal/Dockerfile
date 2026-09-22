FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["LontsiHomes.Portal/LontsiHomes.Portal.csproj", "LontsiHomes.Portal/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "LontsiHomes.Portal/LontsiHomes.Portal.csproj"

COPY . .
RUN dotnet publish "LontsiHomes.Portal/LontsiHomes.Portal.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "LontsiHomes.Portal.dll"]
