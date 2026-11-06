using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Oficina.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly string banco = $"oficina_testes_{Guid.NewGuid():N}";
    private string conexaoAdmin = "";
    public WebApplicationFactory<Program> Api { get; private set; } = null!;
    public string Senha { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public Task InitializeAsync()
    {
        conexaoAdmin = Environment.GetEnvironmentVariable("OFICINA_TESTE_POSTGRES")
            ?? throw new InvalidOperationException("Configure OFICINA_TESTE_POSTGRES com acesso a um PostgreSQL de teste.");
        var conexao = new NpgsqlConnectionStringBuilder(conexaoAdmin) { Database = banco }.ConnectionString;
        Api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Oficina"] = conexao,
                ["Admin:Usuario"] = "admin",
                ["Admin:Senha"] = Senha,
                ["Jwt:Chave"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            })));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Api is null) return;
        await Api.DisposeAsync();
        await using var db = new NpgsqlConnection(conexaoAdmin);
        await db.OpenAsync();
        await using var comando = new NpgsqlCommand($"DROP DATABASE IF EXISTS {banco} WITH (FORCE)", db);
        await comando.ExecuteNonQueryAsync();
    }
}

public sealed class ApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Orcamento_rejeitado_e_reemitido_ate_entrega_com_baixa_de_estoque()
    {
        using var cliente = fixture.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.GetAsync("/api/admin/clientes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.PostAsJsonAsync("/api/auth/token",
            new { usuario = "admin", senha = "incorreta" })).StatusCode);
        using var admin = await Autenticar();
        var dados = await Cadastrar(admin, "529.982.247-25", "AAA1B23");
        var material = await CriarMaterial(admin, 5);
        var aberto = await Abrir(admin, dados, "52998224725", "AAA1B23");
        var rotaAdmin = $"/api/admin/ordens/{aberto.Id}";
        var rotaCliente = $"/api/acompanhar/{aberto.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync(rotaCliente)).StatusCode);
        cliente.DefaultRequestHeaders.Add("X-Codigo-Acompanhamento", aberto.Codigo);
        var acompanhamento = await Ler(await cliente.GetAsync(rotaCliente));
        Assert.Equal("Recebida", acompanhamento.GetProperty("status").GetString());
        Assert.False(acompanhamento.TryGetProperty("clienteId", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"{rotaAdmin}/entrega", null)).StatusCode);

        await Ler(await admin.PostAsync($"{rotaAdmin}/diagnostico", null));
        var orcamento = await Orcar(admin, aberto.Id, material, 2);
        Assert.Equal(160, orcamento.GetProperty("orcamentos")[0].GetProperty("total").GetDecimal());
        await Ler(await admin.PutAsJsonAsync($"/api/admin/materiais/{material}",
            new { nome = "Filtro de óleo", tipo = "Peca", preco = 50 }));
        acompanhamento = await Ler(await cliente.GetAsync(rotaCliente));
        Assert.Equal(160, acompanhamento.GetProperty("orcamento").GetProperty("total").GetDecimal());
        var rejeitada = await Ler(await cliente.PostAsJsonAsync($"{rotaCliente}/decisao", new { revisao = 1, aprovar = false }));
        Assert.Equal("EmDiagnostico", rejeitada.GetProperty("status").GetString());
        await Orcar(admin, aberto.Id, material, 2);

        var desatualizada = await cliente.PostAsJsonAsync($"{rotaCliente}/decisao", new { revisao = 1, aprovar = true });
        Assert.Equal(HttpStatusCode.Conflict, desatualizada.StatusCode);
        var aprovada = await Ler(await cliente.PostAsJsonAsync($"{rotaCliente}/decisao", new { revisao = 2, aprovar = true }));
        Assert.Equal("EmExecucao", aprovada.GetProperty("status").GetString());
        Assert.Equal(180, aprovada.GetProperty("orcamento").GetProperty("total").GetDecimal());

        var estoque = await Ler(await admin.GetAsync($"/api/admin/materiais/{material}"));
        Assert.Equal(5, estoque.GetProperty("saldo").GetInt32());
        Assert.Equal(2, estoque.GetProperty("reservado").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/materiais/{material}")).StatusCode);
        await Ler(await admin.PostAsync($"{rotaAdmin}/finalizacao", null));
        estoque = await Ler(await admin.GetAsync($"/api/admin/materiais/{material}"));
        Assert.Equal(3, estoque.GetProperty("saldo").GetInt32());
        Assert.Equal(0, estoque.GetProperty("reservado").GetInt32());
        var entregue = await Ler(await admin.PostAsync($"{rotaAdmin}/entrega", null));
        Assert.Equal("Entregue", entregue.GetProperty("status").GetString());
        Assert.Equal(2, entregue.GetProperty("orcamentos").GetArrayLength());
        var lista = await Ler(await admin.GetAsync("/api/admin/ordens?status=Entregue"));
        Assert.Contains(lista.EnumerateArray(), x => x.GetProperty("id").GetGuid() == aberto.Id);
        var indicador = await Ler(await admin.GetAsync("/api/admin/indicadores/tempo-medio-execucao"));
        Assert.True(indicador.GetProperty("ordensConcluidas").GetInt32() >= 1);
    }

    [Fact]
    public async Task Duas_ordens_disputam_a_ultima_peca_e_so_uma_reserva()
    {
        using var admin = await Autenticar();
        using var clienteA = fixture.Api.CreateClient();
        using var clienteB = fixture.Api.CreateClient();
        var dados = await Cadastrar(admin, "04.252.011/0001-10", "BBB1234");
        var material = await CriarMaterial(admin, 1);
        var a = await Abrir(admin, dados, "04252011000110", "BBB1234");
        var b = await Abrir(admin, dados, "04252011000110", "BBB1234");
        foreach (var ordem in new[] { a, b })
        {
            await Ler(await admin.PostAsync($"/api/admin/ordens/{ordem.Id}/diagnostico", null));
            await Orcar(admin, ordem.Id, material, 1);
        }

        clienteA.DefaultRequestHeaders.Add("X-Codigo-Acompanhamento", a.Codigo);
        clienteB.DefaultRequestHeaders.Add("X-Codigo-Acompanhamento", b.Codigo);
        Assert.Equal(HttpStatusCode.NotFound, (await clienteA.GetAsync($"/api/acompanhar/{b.Id}")).StatusCode);
        var resultados = await Task.WhenAll(
            clienteA.PostAsJsonAsync($"/api/acompanhar/{a.Id}/decisao", new { revisao = 1, aprovar = true }),
            clienteB.PostAsJsonAsync($"/api/acompanhar/{b.Id}/decisao", new { revisao = 1, aprovar = true }));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], resultados.Select(r => r.StatusCode).Order());
        var perdedora = resultados[0].IsSuccessStatusCode ? b : a;
        var ordemPendente = await Ler(await admin.GetAsync($"/api/admin/ordens/{perdedora.Id}"));
        Assert.Equal("AguardandoAprovacao", ordemPendente.GetProperty("status").GetString());
        var estoque = await Ler(await admin.GetAsync($"/api/admin/materiais/{material}"));
        Assert.Equal(1, estoque.GetProperty("reservado").GetInt32());
        Assert.Equal(0, estoque.GetProperty("disponivel").GetInt32());
    }

    [Fact]
    public async Task Cadastros_validam_dados_duplicidade_e_exclusao_logica()
    {
        using var admin = await Autenticar();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/clientes",
            new { nome = "Cliente", documento = "00000000000" })).StatusCode);
        var cliente = await Ler(await admin.PostAsJsonAsync("/api/admin/clientes",
            new { nome = "Oficina teste", documento = "12.ABC.345/01DE-35" }));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/clientes",
            new { nome = "Duplicado", documento = "12ABC34501DE35" })).StatusCode);
        var id = cliente.GetProperty("id").GetGuid();
        var veiculoInvalido = await admin.PostAsJsonAsync("/api/admin/veiculos",
            new { clienteId = id, placa = "ABCD123", marca = "Fiat", modelo = "Uno", ano = 2015 });
        Assert.Equal(HttpStatusCode.BadRequest, veiculoInvalido.StatusCode);
        var atualizado = await Ler(await admin.PutAsJsonAsync($"/api/admin/clientes/{id}",
            new { nome = "Nome novo", documento = "12ABC34501DE35" }));
        Assert.Equal("Nome novo", atualizado.GetProperty("nome").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/clientes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/clientes/{id}")).StatusCode);
    }

    private async Task<HttpClient> Autenticar()
    {
        var http = fixture.Api.CreateClient();
        var auth = await Ler(await http.PostAsJsonAsync("/api/auth/token", new { usuario = "admin", senha = fixture.Senha }));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("accessToken").GetString());
        return http;
    }

    private static async Task<Guid> Cadastrar(HttpClient admin, string documento, string placa)
    {
        var cliente = await Ler(await admin.PostAsJsonAsync("/api/admin/clientes", new { nome = "Cliente de teste", documento }));
        await Ler(await admin.PostAsJsonAsync("/api/admin/veiculos", new
        {
            clienteId = cliente.GetProperty("id").GetGuid(), placa, marca = "Fiat", modelo = "Uno", ano = 2015
        }));
        var servico = await Ler(await admin.PostAsJsonAsync("/api/admin/servicos", new { nome = "Troca de óleo", preco = 80 }));
        return servico.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CriarMaterial(HttpClient admin, int saldo)
    {
        var material = await Ler(await admin.PostAsJsonAsync("/api/admin/materiais",
            new { nome = "Filtro de óleo", tipo = "Peca", preco = 40 }));
        var id = material.GetProperty("id").GetGuid();
        await Ler(await admin.PostAsJsonAsync($"/api/admin/materiais/{id}/ajustes-estoque", new { quantidade = saldo }));
        return id;
    }

    private static async Task<(Guid Id, string Codigo)> Abrir(HttpClient admin, Guid servico, string documento, string placa)
    {
        var resultado = await Ler(await admin.PostAsJsonAsync("/api/admin/ordens", new { documento, placa, servicos = new[] { servico } }));
        return (resultado.GetProperty("ordem").GetProperty("id").GetGuid(), resultado.GetProperty("codigoAcompanhamento").GetString()!);
    }

    private static async Task<JsonElement> Orcar(HttpClient admin, Guid ordem, Guid material, int quantidade) =>
        await Ler(await admin.PostAsJsonAsync($"/api/admin/ordens/{ordem}/orcamentos",
            new { materiais = new[] { new { materialId = material, quantidade } } }));

    private static async Task<JsonElement> Ler(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.True(resposta.IsSuccessStatusCode, $"{(int)resposta.StatusCode} {resposta.RequestMessage?.RequestUri}: {corpo}");
            using var json = JsonDocument.Parse(corpo);
            return json.RootElement.Clone();
        }
    }
}
