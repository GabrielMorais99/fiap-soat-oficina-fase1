using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Oficina.Aplicacao;
using Oficina.Dominio;
using Oficina.Infraestrutura;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Oficina.Api;

public static class RotasOrdens
{
    public static void MapOrdens(this RouteGroupBuilder admin)
    {
        var rotas = admin.MapGroup("/ordens").WithTags("Ordens de serviço");
        rotas.MapPost("", async (AbrirOrdem entrada, Ordens ordens, CancellationToken ct) =>
        {
            var criada = await ordens.Abrir(entrada, ct);
            return TypedResults.Created($"/api/admin/ordens/{criada.Ordem.Id}", criada);
        }).WithSummary("Receber veículo e abrir uma ordem de serviço");

        rotas.MapGet("", async (OficinaDb db, CancellationToken ct, StatusOrdem? status = null,
            int pagina = 1, int tamanho = 20) =>
        {
            if (pagina is < 1 or > 10_000 || tamanho is < 1 or > 100)
                throw new EntradaInvalida("Informe página de 1 a 10000 e tamanho de 1 a 100.");
            var query = db.Ordens.AsNoTracking();
            if (status is { } filtro) query = query.Where(x => x.Status == filtro);
            return await query.OrderByDescending(x => x.CriadaEm).ThenBy(x => x.Id)
                .Skip((pagina - 1) * tamanho).Take(tamanho)
                .Select(x => new { x.Id, x.ClienteId, x.VeiculoId, x.Status, x.CriadaEm }).ToArrayAsync(ct);
        });

        rotas.MapGet("/{id:guid}", (Guid id, Ordens ordens, CancellationToken ct) => ordens.Detalhar(id, ct));
        rotas.MapPost("/{id:guid}/diagnostico", (Guid id, Ordens ordens, CancellationToken ct) => ordens.Diagnosticar(id, ct));
        rotas.MapPost("/{id:guid}/orcamentos", (Guid id, EmitirOrcamento entrada, Ordens ordens, CancellationToken ct) =>
            ordens.Orcar(id, entrada, ct));
        rotas.MapPost("/{id:guid}/finalizacao", (Guid id, Ordens ordens, CancellationToken ct) => ordens.Finalizar(id, ct));
        rotas.MapPost("/{id:guid}/entrega", (Guid id, Ordens ordens, CancellationToken ct) => ordens.Entregar(id, ct));

        admin.MapGet("/indicadores/tempo-medio-execucao", async (OficinaDb db, CancellationToken ct) =>
        {
            var consulta = db.Ordens.Where(x => x.InicioExecucaoEm != null && x.FimExecucaoEm != null);
            var resultado = await consulta.GroupBy(_ => 1).Select(g => new
            {
                OrdensConcluidas = g.Count(),
                Minutos = g.Average(x => (x.FimExecucaoEm!.Value - x.InicioExecucaoEm!.Value).TotalMinutes)
            }).SingleOrDefaultAsync(ct);
            return new TempoMedio(resultado?.OrdensConcluidas ?? 0, resultado is null ? null : Math.Round(resultado.Minutos, 2));
        }).WithTags("Indicadores");
    }

    public static void MapAcompanhamento(this WebApplication app)
    {
        var rotas = app.MapGroup("/api/acompanhar/{id:guid}").WithTags("Acompanhamento do cliente");
        rotas.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        rotas.MapGet("", (Guid id, [FromHeader(Name = "X-Codigo-Acompanhamento")] string? codigo,
            Ordens ordens, CancellationToken ct) => ordens.Acompanhar(id, codigo, ct));
        rotas.MapPost("/decisao", (Guid id, [FromHeader(Name = "X-Codigo-Acompanhamento")] string? codigo,
            DecidirOrcamento entrada, Ordens ordens, CancellationToken ct) => ordens.Decidir(id, codigo, entrada, ct));
    }
}

public sealed record TempoMedio(int OrdensConcluidas, double? Minutos);

public sealed class SegurancaSwagger : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any()) return;
        operation.Security.Add(new()
        {
            [new() { Reference = new() { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
        });
    }
}
