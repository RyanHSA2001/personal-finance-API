using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using PersonalControl.Api.Auth;
using PersonalControl.Api.Cards;
using PersonalControl.Api.Data;
using PersonalControl.Api.Finance;
using PersonalControl.Api.OpenFinance;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Respeita X-Forwarded-For e X-Forwarded-Proto quando rodando atrás de Nginx/reverse proxy na VPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Proteção contra força bruta nos endpoints de autenticação.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Muitas tentativas. Aguarde um minuto e tente novamente." },
            cancellationToken);
    };

    options.AddPolicy(AuthEndpoints.StrictRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    options.AddPolicy(AuthEndpoints.RefreshRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// Configuração JWT (seção "Jwt" do appsettings)
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();

if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key precisa ser configurada com pelo menos 32 caracteres.");

// Acesso coringa temporário (seção "WildcardAccess")
builder.Services.Configure<WildcardAccessOptions>(builder.Configuration.GetSection("WildcardAccess"));

// Banco PostgreSQL: conecta via Npgsql e aplica as migrações pendentes na inicialização.
builder.Services.AddDataAccess(builder.Configuration);

// Open Finance via Pluggy (credenciais criptografadas por usuário ou fallback via user-secrets).
builder.Services.Configure<PluggyOptions>(builder.Configuration.GetSection("Pluggy"));
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddHttpClient<PluggyApiClient>((sp, client) =>
{
    var baseUrl = builder.Configuration["Pluggy:BaseUrl"] ?? "https://api.pluggy.ai";
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddScoped<OpenFinanceSyncService>();

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<JwtTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapFinanceEndpoints();
app.MapCardEndpoints();
app.MapOpenFinanceEndpoints();

app.Run();
