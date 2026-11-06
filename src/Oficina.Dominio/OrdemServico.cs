namespace Oficina.Dominio;

public enum StatusOrdem { Recebida, EmDiagnostico, AguardandoAprovacao, EmExecucao, Finalizada, Entregue }
public enum SituacaoOrcamento { Pendente, Aprovado, Rejeitado }
public enum TipoItem { Servico, Material }

public sealed record ItemOrcamento(Guid ReferenciaId, TipoItem Tipo, string Nome, int Quantidade, decimal PrecoUnitario)
{
    public decimal Subtotal => Quantidade * PrecoUnitario;
}

public sealed record Orcamento(int Revisao, DateTimeOffset EmitidoEm, ItemOrcamento[] Itens,
    SituacaoOrcamento Situacao = SituacaoOrcamento.Pendente, DateTimeOffset? DecididoEm = null)
{
    public decimal Total => Itens.Sum(item => item.Subtotal);
}

public sealed record EventoOrdem(StatusOrdem Status, string Evento, DateTimeOffset Em);

public sealed class OrdemServico
{
    private OrdemServico() { }

    public OrdemServico(Guid clienteId, Guid veiculoId, Guid[] servicos, string tokenHash, DateTimeOffset agora)
    {
        if (servicos.Length == 0) throw new EntradaInvalida("Informe pelo menos um serviço solicitado.");
        ClienteId = clienteId;
        VeiculoId = veiculoId;
        ServicosSolicitados = servicos;
        TokenHash = tokenHash;
        CriadaEm = agora;
        Transicionar(StatusOrdem.Recebida, "OrdemRecebida", agora);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ClienteId { get; private set; }
    public Guid VeiculoId { get; private set; }
    public Guid[] ServicosSolicitados { get; private set; } = [];
    public string TokenHash { get; private set; } = "";
    public Guid Versao { get; private set; } = Guid.NewGuid();
    public StatusOrdem Status { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? InicioExecucaoEm { get; private set; }
    public DateTimeOffset? FimExecucaoEm { get; private set; }
    public Orcamento[] Orcamentos { get; private set; } = [];
    public EventoOrdem[] Historico { get; private set; } = [];

    public void IniciarDiagnostico(DateTimeOffset agora)
    {
        Exigir(StatusOrdem.Recebida);
        Transicionar(StatusOrdem.EmDiagnostico, "DiagnosticoIniciado", agora);
    }

    public void EmitirOrcamento(ItemOrcamento[] itens, DateTimeOffset agora)
    {
        Exigir(StatusOrdem.EmDiagnostico);
        if (!itens.Any(x => x.Tipo == TipoItem.Servico))
            throw new EntradaInvalida("O orçamento precisa de pelo menos um serviço.");
        Orcamentos = [.. Orcamentos, new(Orcamentos.Length + 1, agora, itens)];
        Transicionar(StatusOrdem.AguardandoAprovacao, "OrcamentoDisponibilizado", agora);
    }

    public void DecidirOrcamento(int revisao, bool aprovar, DateTimeOffset agora)
    {
        Exigir(StatusOrdem.AguardandoAprovacao);
        var atual = Orcamentos[^1];
        if (atual.Revisao != revisao) throw new RegraViolada("A revisão do orçamento mudou.");
        Orcamentos = [.. Orcamentos[..^1], atual with
        {
            Situacao = aprovar ? SituacaoOrcamento.Aprovado : SituacaoOrcamento.Rejeitado,
            DecididoEm = agora
        }];
        if (aprovar)
        {
            InicioExecucaoEm = agora;
            Transicionar(StatusOrdem.EmExecucao, "OrcamentoAprovado", agora);
        }
        else
        {
            Transicionar(StatusOrdem.EmDiagnostico, "OrcamentoRejeitado", agora);
        }
    }

    public void Finalizar(DateTimeOffset agora)
    {
        Exigir(StatusOrdem.EmExecucao);
        FimExecucaoEm = agora;
        Transicionar(StatusOrdem.Finalizada, "ExecucaoFinalizada", agora);
    }

    public void Entregar(DateTimeOffset agora)
    {
        Exigir(StatusOrdem.Finalizada);
        Transicionar(StatusOrdem.Entregue, "VeiculoEntregue", agora);
    }

    private void Exigir(StatusOrdem esperado)
    {
        if (Status != esperado)
            throw new RegraViolada($"A ação exige o status {esperado}. Status atual: {Status}.");
    }

    private void Transicionar(StatusOrdem status, string evento, DateTimeOffset agora)
    {
        Status = status;
        Versao = Guid.NewGuid();
        Historico = [.. Historico, new(status, evento, agora)];
    }
}
