using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using Oficina.Api;
using Oficina.Aplicacao;
using Oficina.Dominio;
using Oficina.Infraestrutura;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ConfiguracaoJwt>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Ordens>();
builder.Services.AddDbContext<OficinaDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Oficina") ??
    throw new InvalidOperationException("Configure ConnectionStrings__Oficina.")));
builder.Services.AddScoped<IDadosOrdens>(sp => sp.GetRequiredService<OficinaDb>());
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<ConfiguracaoJwt>((options, jwt) =>
{
    options.TokenValidationParameters = new()
    {
        ValidIssuer = ConfiguracaoJwt.Emissor,
        ValidAudience = ConfiguracaoJwt.Publico,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = jwt.Chave,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Administracao", policy => policy.RequireRole("admin")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new() { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Oficina API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new()
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Token obtido em POST /api/auth/token."
    });
    options.OperationFilter<SegurancaSwagger>();
});

var app = builder.Build();
_ = app.Services.GetRequiredService<ConfiguracaoJwt>();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        var (status, detalhe) = e switch
        {
            EntradaInvalida => (400, e.Message),
            NaoEncontrado => (404, e.Message),
            RegraViolada => (409, e.Message),
            BadHttpRequestException erro => (erro.StatusCode, "Verifique o corpo e os parâmetros da requisição."),
            DbUpdateConcurrencyException => (409, "O registro foi alterado. Consulte a versão atual e tente novamente."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                => (409, "Já existe um cadastro com esse CPF, CNPJ ou placa."),
            _ => (500, "Ocorreu um erro interno.")
        };
        if (e is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
        if (status == 500) app.Logger.LogError(e, "Erro ao processar a requisição {TraceId}", context.TraceIdentifier);
        await Results.Problem(statusCode: status, detail: detalhe).ExecuteAsync(context);
    }
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapGet("/health", async (OficinaDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503))
    .WithTags("Saúde");
app.MapPost("/api/auth/token", (Login entrada, ConfiguracaoJwt auth, TimeProvider relogio) =>
    auth.Autenticar(entrada, relogio) is { } token ? Results.Ok(token) : Results.Unauthorized())
    .RequireRateLimiting("login").WithTags("Autenticação")
    .Produces<TokenAcesso>().Produces(StatusCodes.Status401Unauthorized);

var admin = app.MapGroup("/api/admin").RequireAuthorization("Administracao");
admin.MapCadastros();
admin.MapOrdens();
app.MapAcompanhamento();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<OficinaDb>().Database.EnsureCreatedAsync();
await app.RunAsync();

public partial class Program;

namespace Oficina.Api
{
    public sealed record Login(string Usuario, string Senha);
    public sealed record TokenAcesso(string AccessToken, string TokenType, int ExpiresIn);

    public sealed class ConfiguracaoJwt
    {
        public const string Emissor = "Oficina.Api";
        public const string Publico = "Oficina.Admin";
        private readonly string usuario;
        private readonly byte[] senhaHash;
        public SymmetricSecurityKey Chave { get; }

        public ConfiguracaoJwt(IConfiguration config)
        {
            usuario = config["Admin:Usuario"] ?? "admin";
            var senha = config["Admin:Senha"];
            var chave = Encoding.UTF8.GetBytes(config["Jwt:Chave"] ?? "");
            if (senha is null || senha.Length < 12 || chave.Length < 32)
                throw new InvalidOperationException("Configure Admin__Senha (12 caracteres) e Jwt__Chave (32 bytes).");
            senhaHash = Hash(senha);
            Chave = new(chave);
        }

        public TokenAcesso? Autenticar(Login entrada, TimeProvider relogio)
        {
            if (!CryptographicOperations.FixedTimeEquals(senhaHash, Hash(entrada.Senha ?? "")) ||
                !string.Equals(entrada.Usuario, usuario, StringComparison.Ordinal)) return null;

            var agora = relogio.GetUtcNow().UtcDateTime;
            var jwt = new JwtSecurityToken(Emissor, Publico,
                [new(ClaimTypes.NameIdentifier, usuario), new(ClaimTypes.Role, "admin")],
                agora, agora.AddMinutes(30), new(Chave, SecurityAlgorithms.HmacSha256));
            return new(new JwtSecurityTokenHandler().WriteToken(jwt), "Bearer", 1800);
        }

        private static byte[] Hash(string texto) => SHA256.HashData(Encoding.UTF8.GetBytes(texto));
    }
}
