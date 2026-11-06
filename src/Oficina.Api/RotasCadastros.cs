using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Oficina.Aplicacao;
using Oficina.Dominio;
using Oficina.Infraestrutura;

namespace Oficina.Api;

public static class RotasCadastros
{
    public static void MapCadastros(this RouteGroupBuilder admin)
    {
        MapCadastro<Cliente, ClienteEntrada, ClienteSaida>(admin, "clientes", "Clientes",
            (entrada, _, _, _) => ValueTask.FromResult(new Cliente(entrada.Nome, entrada.Documento)),
            (cliente, entrada, _) => cliente.Atualizar(entrada.Nome, entrada.Documento),
            cliente => cliente.Desativar(),
            cliente => new(cliente.Id, cliente.Nome, cliente.Documento.Valor));

        MapCadastro<Veiculo, VeiculoEntrada, VeiculoSaida>(admin, "veiculos", "Veículos",
            async (entrada, db, relogio, ct) =>
            {
                if (!await db.Clientes.AnyAsync(x => x.Id == entrada.ClienteId && x.Ativo, ct))
                    throw new NaoEncontrado("Cliente ativo não encontrado.");
                return new(entrada.ClienteId, entrada.Placa, entrada.Marca, entrada.Modelo, entrada.Ano, relogio.GetUtcNow().Year);
            },
            (veiculo, entrada, relogio) =>
            {
                if (entrada.ClienteId != veiculo.ClienteId) throw new RegraViolada("O proprietário do veículo é fixo nesta versão.");
                veiculo.Atualizar(entrada.Placa, entrada.Marca, entrada.Modelo, entrada.Ano, relogio.GetUtcNow().Year);
            },
            veiculo => veiculo.Desativar(),
            veiculo => new(veiculo.Id, veiculo.ClienteId, veiculo.Placa.Valor, veiculo.Marca, veiculo.Modelo, veiculo.Ano));

        MapCadastro<Servico, ServicoEntrada, ServicoSaida>(admin, "servicos", "Serviços",
            (entrada, _, _, _) => ValueTask.FromResult(new Servico(entrada.Nome, entrada.Preco)),
            (servico, entrada, _) => servico.Atualizar(entrada.Nome, entrada.Preco),
            servico => servico.Desativar(),
            servico => new(servico.Id, servico.Nome, servico.Preco));

        MapCadastro<Material, MaterialEntrada, MaterialSaida>(admin, "materiais", "Peças e insumos",
            (entrada, _, _, _) => ValueTask.FromResult(new Material(entrada.Nome, entrada.Tipo, entrada.Preco)),
            (material, entrada, _) =>
            {
                if (entrada.Tipo != material.Tipo) throw new RegraViolada("O tipo do material é fixo nesta versão.");
                material.Atualizar(entrada.Nome, entrada.Preco);
            },
            material => material.Desativar(),
            MostrarMaterial);

        admin.MapPost("/materiais/{id:guid}/ajustes-estoque", async (Guid id, AjusteEstoque entrada,
            OficinaDb db, CancellationToken ct) =>
        {
            var material = await Buscar<Material>(id, db, ct);
            material.AjustarSaldo(entrada.Quantidade);
            await db.SaveChangesAsync(ct);
            return MostrarMaterial(material);
        }).WithTags("Peças e insumos");
    }

    private static void MapCadastro<T, TEntrada, TSaida>(RouteGroupBuilder admin, string segmento, string tag,
        Func<TEntrada, OficinaDb, TimeProvider, CancellationToken, ValueTask<T>> criar,
        Action<T, TEntrada, TimeProvider> atualizar, Action<T> desativar, Func<T, TSaida> mostrar)
        where T : Cadastro where TEntrada : class
    {
        var rotas = admin.MapGroup($"/{segmento}").WithTags(tag);
        rotas.MapGet("", async (OficinaDb db, CancellationToken ct, int pagina = 1, int tamanho = 20) =>
        {
            if (pagina is < 1 or > 10_000 || tamanho is < 1 or > 100)
                throw new EntradaInvalida("Informe página de 1 a 10000 e tamanho de 1 a 100.");
            var registros = await db.Set<T>().AsNoTracking().Where(x => x.Ativo).OrderBy(x => x.Id)
                .Skip((pagina - 1) * tamanho).Take(tamanho).ToArrayAsync(ct);
            return registros.Select(mostrar);
        });
        rotas.MapGet("/{id:guid}", async (Guid id, OficinaDb db, CancellationToken ct) =>
            mostrar(await Buscar<T>(id, db, ct)));
        rotas.MapPost("", async ([FromBody] TEntrada entrada, OficinaDb db, TimeProvider relogio, CancellationToken ct) =>
        {
            var registro = await criar(entrada, db, relogio, ct);
            db.Add(registro);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/api/admin/{segmento}/{registro.Id}", mostrar(registro));
        });
        rotas.MapPut("/{id:guid}", async (Guid id, [FromBody] TEntrada entrada, OficinaDb db, TimeProvider relogio, CancellationToken ct) =>
        {
            var registro = await Buscar<T>(id, db, ct);
            atualizar(registro, entrada, relogio);
            await db.SaveChangesAsync(ct);
            return mostrar(registro);
        });
        rotas.MapDelete("/{id:guid}", async (Guid id, OficinaDb db, CancellationToken ct) =>
        {
            desativar(await Buscar<T>(id, db, ct));
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        });
    }

    private static async Task<T> Buscar<T>(Guid id, OficinaDb db, CancellationToken ct) where T : Cadastro =>
        await db.Set<T>().SingleOrDefaultAsync(x => x.Id == id && x.Ativo, ct)
        ?? throw new NaoEncontrado("Cadastro ativo não encontrado.");

    private static MaterialSaida MostrarMaterial(Material x) =>
        new(x.Id, x.Nome, x.Tipo, x.Preco, x.Saldo, x.Reservado, x.Disponivel);
}

public sealed record ClienteEntrada(string Nome, string Documento);
public sealed record ClienteSaida(Guid Id, string Nome, string Documento);
public sealed record VeiculoEntrada(Guid ClienteId, string Placa, string Marca, string Modelo, int Ano);
public sealed record VeiculoSaida(Guid Id, Guid ClienteId, string Placa, string Marca, string Modelo, int Ano);
public sealed record ServicoEntrada(string Nome, decimal Preco);
public sealed record ServicoSaida(Guid Id, string Nome, decimal Preco);
public sealed record MaterialEntrada(string Nome, TipoMaterial Tipo, decimal Preco);
public sealed record MaterialSaida(Guid Id, string Nome, TipoMaterial Tipo, decimal Preco, int Saldo, int Reservado, int Disponivel);
public sealed record AjusteEstoque(int Quantidade);
