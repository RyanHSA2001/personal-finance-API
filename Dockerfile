FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine
WORKDIR /app
COPY ./publish .
RUN rm -f /app/appsettings.Development.json

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PersonalControl.Api.dll"]
