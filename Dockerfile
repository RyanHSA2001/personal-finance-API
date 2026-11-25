FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Copia o csproj e restaura dependências
COPY ["PersonalControl.Api.csproj", "./"]
RUN dotnet restore "PersonalControl.Api.csproj"

# Copia o restante do código e compila
COPY . .
RUN dotnet publish "PersonalControl.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Imagem final de execução
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "PersonalControl.Api.dll"]
