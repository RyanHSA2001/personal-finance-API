FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine
WORKDIR /app
COPY ./publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "PersonalControl.Api.dll"]
