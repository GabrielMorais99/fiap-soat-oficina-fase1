using System.Security.Cryptography;
using System.Text;
using Oficina.Dominio;

namespace Oficina.Aplicacao;

public sealed class NaoEncontrado(string mensagem) : Exception(mensagem);

public interface IDadosOrdens
{
    Task<Cliente?> BuscarCliente(Documento documento, CancellationToken ct);
    Task<Veiculo?> BuscarVeiculo(Placa placa, CancellationToken ct);
    Task<Servico[]> BuscarServicos(Guid[] ids, CancellationToken ct);
    Task<Material[]> BuscarMateriais(Guid[] ids, CancellationToken ct);
    Task<OrdemServico?> BuscarOrdem(Guid id, CancellationToken ct);
    void Adicionar(OrdemServico ordem);
    Task Salvar(CancellationToken ct);
}

public sealed record AbrirOrdem(string Documento, string Placa, Guid[] Servicos);
public sealed record MaterialSolicitado(Guid MaterialId, int Quantidade);
public sealed record EmitirOrcamento(Guid[]? Servicos, MaterialSolicitado[]? Materiais);
public sealed record DecidirOrcamento(int Revisao, bool? Aprovar);

public sealed record OrdemDetalhe(Guid Id, Guid ClienteId, Guid VeiculoId, StatusOrdem Status,
    DateTimeOffset CriadaEm, DateTimeOffset? InicioExecucaoEm, DateTimeOffset? FimExecucaoEm,
    Guid[] ServicosSolicitados, Orcamento[] Orcamentos, EventoOrdem[] Historico)
{
    public static OrdemDetalhe De(OrdemServico ordem) => new(ordem.Id, ordem.ClienteId, ordem.VeiculoId,
        ordem.Status, ordem.CriadaEm, ordem.InicioExecucaoEm, ordem.FimExecucaoEm,
        ordem.ServicosSolicitados, ordem.Orcamentos, ordem.Historico);
}

public sealed record Acompanhamento(Guid Id, StatusOrdem Status, Orcamento? Orcamento, EventoOrdem[] Historico)
{
    public static Acompanhamento De(OrdemServico ordem) =>
        new(ordem.Id, ordem.Status, ordem.Orcamentos.LastOrDefault(), ordem.Historico);
}

public sealed record OrdemAberta(OrdemDetalhe Ordem, string CodigoAcompanhamento, string CaminhoAcompanhamento);

public sealed class Ordens(IDadosOrdens dados, TimeProvider relogio)
{
    public async Task<OrdemAberta> Abrir(AbrirOrdem entrada, CancellationToken ct)
    {
        var cliente = await dados.BuscarCliente(new(entrada.Documento), ct)
            ?? throw new NaoEncontrado("Cliente ativo não encontrado.");
        var veiculo = await dados.BuscarVeiculo(new(entrada.Placa), ct);
        if (veiculo is null || veiculo.ClienteId != cliente.Id)
            throw new NaoEncontrado("Veículo ativo não encontrado para este cliente.");

        var ids = ValidarServicos(entrada.Servicos);
        await CarregarServicos(ids, ct);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var ordem = new OrdemServico(cliente.Id, veiculo.Id, ids, Hash(token), relogio.GetUtcNow());
        dados.Adicionar(ordem);
        await dados.Salvar(ct);
        return new(OrdemDetalhe.De(ordem), token, $"/api/acompanhar/{ordem.Id}");
    }

    public async Task<OrdemDetalhe> Detalhar(Guid id, CancellationToken ct) =>
        OrdemDetalhe.De(await Obter(id, ct));

    public async Task<OrdemDetalhe> Diagnosticar(Guid id, CancellationToken ct)
    {
        var ordem = await Obter(id, ct);
        ordem.IniciarDiagnostico(relogio.GetUtcNow());
        return await Salvar(ordem, ct);
    }

    public async Task<OrdemDetalhe> Orcar(Guid id, EmitirOrcamento entrada, CancellationToken ct)
    {
        var ordem = await Obter(id, ct);
        var servicos = await CarregarServicos(ValidarServicos(entrada.Servicos ?? ordem.ServicosSolicitados), ct);
        var itens = servicos.Select(s => new ItemOrcamento(s.Id, TipoItem.Servico, s.Nome, 1, s.Preco)).ToList();
        var solicitados = entrada.Materiais ?? [];

        if (solicitados.Length > 100 || solicitados.Any(x => x is null || x.Quantidade is < 1 or > 10_000) ||
            solicitados.Select(x => x.MaterialId).Distinct().Count() != solicitados.Length)
            throw new EntradaInvalida("Informe até 100 materiais distintos com quantidades entre 1 e 10000.");

        var materiais = await dados.BuscarMateriais(solicitados.Select(x => x.MaterialId).ToArray(), ct);
        var materiaisPorId = materiais.Where(x => x.Ativo).ToDictionary(x => x.Id);
        foreach (var item in solicitados)
        {
            if (!materiaisPorId.TryGetValue(item.MaterialId, out var material))
                throw new NaoEncontrado("Material ativo não encontrado.");
            itens.Add(new(material.Id, TipoItem.Material, material.Nome, item.Quantidade, material.Preco));
        }

        ordem.EmitirOrcamento(itens.ToArray(), relogio.GetUtcNow());
        return await Salvar(ordem, ct);
    }

    public async Task<Acompanhamento> Acompanhar(Guid id, string? token, CancellationToken ct) =>
        Acompanhamento.De(await ObterComToken(id, token, ct));

    public async Task<Acompanhamento> Decidir(Guid id, string? token, DecidirOrcamento entrada, CancellationToken ct)
    {
        var ordem = await ObterComToken(id, token, ct);
        if (entrada.Aprovar is not bool aprovar) throw new EntradaInvalida("Informe se aprova o orçamento.");

        ordem.DecidirOrcamento(entrada.Revisao, aprovar, relogio.GetUtcNow());
        if (aprovar)
        {
            foreach (var (material, quantidade) in await MateriaisDaOrdem(ordem, ct))
                material.Reservar(quantidade);
        }

        await dados.Salvar(ct);
        return Acompanhamento.De(ordem);
    }

    public async Task<OrdemDetalhe> Finalizar(Guid id, CancellationToken ct)
    {
        var ordem = await Obter(id, ct);
        ordem.Finalizar(relogio.GetUtcNow());
        foreach (var (material, quantidade) in await MateriaisDaOrdem(ordem, ct))
            material.BaixarReserva(quantidade);
        return await Salvar(ordem, ct);
    }

    public async Task<OrdemDetalhe> Entregar(Guid id, CancellationToken ct)
    {
        var ordem = await Obter(id, ct);
        ordem.Entregar(relogio.GetUtcNow());
        return await Salvar(ordem, ct);
    }

    private async Task<(Material Material, int Quantidade)[]> MateriaisDaOrdem(OrdemServico ordem, CancellationToken ct)
    {
        var itens = ordem.Orcamentos[^1].Itens.Where(x => x.Tipo == TipoItem.Material).ToArray();
        var materiais = await dados.BuscarMateriais(itens.Select(x => x.ReferenciaId).ToArray(), ct);
        var porId = materiais.ToDictionary(x => x.Id);
        return itens.Select(x => (porId[x.ReferenciaId], x.Quantidade)).ToArray();
    }

    private async Task<OrdemServico> Obter(Guid id, CancellationToken ct) =>
        await dados.BuscarOrdem(id, ct) ?? throw new NaoEncontrado("Ordem de serviço não encontrada.");

    private async Task<OrdemServico> ObterComToken(Guid id, string? token, CancellationToken ct)
    {
        if (token is null || token.Length != 64) throw new NaoEncontrado("Acompanhamento não encontrado.");
        var ordem = await dados.BuscarOrdem(id, ct);
        if (ordem is null || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(ordem.TokenHash), Convert.FromHexString(Hash(token))))
            throw new NaoEncontrado("Acompanhamento não encontrado.");
        return ordem;
    }

    private async Task<OrdemDetalhe> Salvar(OrdemServico ordem, CancellationToken ct)
    {
        await dados.Salvar(ct);
        return OrdemDetalhe.De(ordem);
    }

    private async Task<Servico[]> CarregarServicos(Guid[] ids, CancellationToken ct)
    {
        var servicos = await dados.BuscarServicos(ids, ct);
        if (servicos.Length != ids.Length) throw new NaoEncontrado("Serviço ativo não encontrado.");
        var porId = servicos.ToDictionary(x => x.Id);
        return ids.Select(id => porId[id]).ToArray();
    }

    private static Guid[] ValidarServicos(Guid[]? ids)
    {
        if (ids is null || ids.Length is < 1 or > 100 || ids.Distinct().Count() != ids.Length)
            throw new EntradaInvalida("Informe de 1 a 100 serviços distintos.");
        return ids;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
