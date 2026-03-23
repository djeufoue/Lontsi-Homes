FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["RentHub.Portal/RentHub.Portal.csproj", "RentHub.Portal/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "RentHub.Portal/RentHub.Portal.csproj"

COPY . .
RUN dotnet publish "RentHub.Portal/RentHub.Portal.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "RentHub.Portal.dll"]
