using Oficina.Dominio;
using Xunit;

namespace Oficina.Tests;

public sealed class DominioTests
{
    private static readonly DateTimeOffset Agora = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData("04.252.011/0001-10", "04252011000110")]
    [InlineData("12.abc.345/01de-35", "12ABC34501DE35")]
    public void Normaliza_documentos_validos(string entrada, string esperado) =>
        Assert.Equal(esperado, new Documento(entrada).Valor);

    [Theory]
    [InlineData(null)]
    [InlineData("52998224726")]
    [InlineData("00000000000")]
    [InlineData("04252011000111")]
    [InlineData("12.ABC.345/01DE-34")]
    [InlineData("529x98224725")]
    public void Rejeita_documentos_invalidos(string? entrada) =>
        Assert.Throws<EntradaInvalida>(() => new Documento(entrada));

    [Theory]
    [InlineData("abc-1234", "ABC1234")]
    [InlineData("abc1d23", "ABC1D23")]
    public void Normaliza_placas_validas(string entrada, string esperado) =>
        Assert.Equal(esperado, new Placa(entrada).Valor);

    [Theory]
    [InlineData(null)]
    [InlineData("ABCD123")]
    [InlineData("ABC-1D23")]
    public void Rejeita_placas_invalidas(string? entrada) =>
        Assert.Throws<EntradaInvalida>(() => new Placa(entrada));

    [Fact]
    public void Cadastros_validam_preco_texto_e_ano()
    {
        Assert.Throws<EntradaInvalida>(() => new Cliente(" ", "52998224725"));
        Assert.Throws<EntradaInvalida>(() => new Servico("Alinhamento", -1));
        Assert.Throws<EntradaInvalida>(() => new Servico("Alinhamento", 1.001m));
        Assert.Throws<EntradaInvalida>(() => new Servico("Alinhamento", 1_000_000m));
        Assert.Throws<EntradaInvalida>(() => new Veiculo(Guid.NewGuid(), "ABC1234", "Fiat", "Uno", 2030, 2026));
        Assert.Throws<EntradaInvalida>(() => new Material("Filtro", (TipoMaterial)50, 1));
    }

    [Fact]
    public void Reserva_reduz_disponivel_e_baixa_reduz_saldo()
    {
        var material = new Material("Filtro", TipoMaterial.Peca, 40);
        material.AjustarSaldo(5);
        material.Reservar(3);
        Assert.Equal(2, material.Disponivel);
        Assert.Throws<RegraViolada>(() => material.AjustarSaldo(-3));
        Assert.Throws<RegraViolada>(() => material.Reservar(3));
        Assert.Throws<RegraViolada>(() => material.Desativar());
        Assert.Throws<RegraViolada>(() => material.BaixarReserva(4));
        material.BaixarReserva(3);
        Assert.Equal(2, material.Saldo);
        Assert.Equal(0, material.Reservado);
        material.AjustarSaldo(-2);
        material.Desativar();
        Assert.False(material.Ativo);
        Assert.Throws<RegraViolada>(() => material.Reservar(1));
        Assert.Throws<EntradaInvalida>(() => material.AjustarSaldo(0));
    }

    [Fact]
    public void Ordem_percorre_o_fluxo_e_mantem_revisoes()
    {
        var servico = new ItemOrcamento(Guid.NewGuid(), TipoItem.Servico, "Troca de óleo", 1, 80);
        var oleo = new ItemOrcamento(Guid.NewGuid(), TipoItem.Material, "Óleo 1L", 4, 35);
        var ordem = new OrdemServico(Guid.NewGuid(), Guid.NewGuid(), [servico.ReferenciaId], "hash", Agora);
        Assert.Equal(StatusOrdem.Recebida, ordem.Status);
        Assert.Throws<RegraViolada>(() => ordem.Entregar(Agora));
        Assert.Throws<RegraViolada>(() => ordem.EmitirOrcamento([servico], Agora));
        ordem.IniciarDiagnostico(Agora);
        Assert.Throws<EntradaInvalida>(() => ordem.EmitirOrcamento([oleo], Agora));
        ordem.EmitirOrcamento([servico, oleo], Agora);
        Assert.Equal(220, ordem.Orcamentos[0].Total);
        ordem.DecidirOrcamento(1, false, Agora);
        Assert.Equal(StatusOrdem.EmDiagnostico, ordem.Status);
        Assert.Equal(SituacaoOrcamento.Rejeitado, ordem.Orcamentos[0].Situacao);
        ordem.EmitirOrcamento([servico], Agora);
        Assert.Throws<RegraViolada>(() => ordem.DecidirOrcamento(1, true, Agora));
        ordem.DecidirOrcamento(2, true, Agora);
        Assert.Equal(StatusOrdem.EmExecucao, ordem.Status);
        Assert.Equal(Agora, ordem.InicioExecucaoEm);
        Assert.Throws<RegraViolada>(() => ordem.DecidirOrcamento(2, true, Agora));
        ordem.Finalizar(Agora.AddMinutes(45));
        Assert.Equal(Agora.AddMinutes(45), ordem.FimExecucaoEm);
        ordem.Entregar(Agora.AddMinutes(50));
        Assert.Equal(StatusOrdem.Entregue, ordem.Status);
        Assert.Equal(8, ordem.Historico.Length);
    }

    [Fact]
    public void Ordem_exige_servico()
    {
        Assert.Throws<EntradaInvalida>(() => new OrdemServico(Guid.NewGuid(), Guid.NewGuid(), [], "hash", Agora));
    }
}
