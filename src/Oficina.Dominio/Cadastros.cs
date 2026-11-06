using System.Text.RegularExpressions;

namespace Oficina.Dominio;

public sealed class EntradaInvalida(string mensagem) : Exception(mensagem);
public sealed class RegraViolada(string mensagem) : Exception(mensagem);

public static class Validacao
{
    public static string Texto(string? valor, int limite, string campo)
    {
        if (string.IsNullOrWhiteSpace(valor) || valor.Trim().Length > limite)
            throw new EntradaInvalida($"Informe {campo} com até {limite} caracteres.");
        return valor.Trim();
    }

    public static decimal Preco(decimal valor)
    {
        if (valor < 0 || valor > 999_999.99m || decimal.Round(valor, 2) != valor)
            throw new EntradaInvalida("Informe um preço entre 0 e 999999,99 com até duas casas decimais.");
        return valor;
    }
}

public sealed record Documento
{
    public string Valor { get; }

    public Documento(string? valor)
    {
        var texto = (valor ?? "").Trim().ToUpperInvariant();
        if (Regex.IsMatch(texto, @"\A[0-9]{3}\.[0-9]{3}\.[0-9]{3}-[0-9]{2}\z") ||
            Regex.IsMatch(texto, @"\A[A-Z0-9]{2}\.[A-Z0-9]{3}\.[A-Z0-9]{3}/[A-Z0-9]{4}-[0-9]{2}\z"))
            texto = texto.Replace(".", "").Replace("/", "").Replace("-", "");

        var valido = texto.Length switch
        {
            11 => Regex.IsMatch(texto, @"\A[0-9]{11}\z") && texto.Distinct().Count() > 1 &&
                  Digito(texto[..9], [10, 9, 8, 7, 6, 5, 4, 3, 2]) == texto[9] &&
                  Digito(texto[..10], [11, 10, 9, 8, 7, 6, 5, 4, 3, 2]) == texto[10],
            14 => Regex.IsMatch(texto, @"\A[A-Z0-9]{12}[0-9]{2}\z") && texto.Distinct().Count() > 1 &&
                  Digito(texto[..12], [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == texto[12] &&
                  Digito(texto[..13], [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]) == texto[13],
            _ => false
        };
        if (!valido) throw new EntradaInvalida("CPF ou CNPJ inválido.");
        Valor = texto;
    }

    private static char Digito(string baseDocumento, int[] pesos)
    {
        var resto = baseDocumento.Zip(pesos, (c, peso) => (c - '0') * peso).Sum() % 11;
        return (char)('0' + (resto < 2 ? 0 : 11 - resto));
    }
}

public sealed record Placa
{
    public string Valor { get; }

    public Placa(string? valor)
    {
        var texto = (valor ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(texto, @"\A[A-Z]{3}(?:-?[0-9]{4}|[0-9][A-Z][0-9]{2})\z"))
            throw new EntradaInvalida("Placa inválida. Use ABC1234 ou ABC1D23.");
        Valor = texto.Replace("-", "");
    }
}

public abstract class Cadastro
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public bool Ativo { get; private set; } = true;
    public virtual void Desativar() => Ativo = false;
}

public sealed class Cliente : Cadastro
{
    private Cliente() { }
    public Cliente(string? nome, string? documento) => Atualizar(nome, documento);
    public string Nome { get; private set; } = "";
    public Documento Documento { get; private set; } = null!;

    public void Atualizar(string? nome, string? documento)
    {
        var novoDocumento = new Documento(documento);
        Nome = Validacao.Texto(nome, 120, "o nome");
        Documento = novoDocumento;
    }
}

public sealed class Veiculo : Cadastro
{
    private Veiculo() { }
    public Veiculo(Guid clienteId, string? placa, string? marca, string? modelo, int ano, int anoAtual)
    {
        ClienteId = clienteId;
        Atualizar(placa, marca, modelo, ano, anoAtual);
    }

    public Guid ClienteId { get; private set; }
    public Placa Placa { get; private set; } = null!;
    public string Marca { get; private set; } = "";
    public string Modelo { get; private set; } = "";
    public int Ano { get; private set; }

    public void Atualizar(string? placa, string? marca, string? modelo, int ano, int anoAtual)
    {
        if (ano < 1886 || ano > anoAtual + 1) throw new EntradaInvalida("Ano do veículo inválido.");
        var novaPlaca = new Placa(placa);
        var novaMarca = Validacao.Texto(marca, 60, "a marca");
        Modelo = Validacao.Texto(modelo, 80, "o modelo");
        Placa = novaPlaca;
        Marca = novaMarca;
        Ano = ano;
    }
}

public sealed class Servico : Cadastro
{
    private Servico() { }
    public Servico(string? nome, decimal preco) => Atualizar(nome, preco);
    public string Nome { get; private set; } = "";
    public decimal Preco { get; private set; }

    public void Atualizar(string? nome, decimal preco)
    {
        var novoPreco = Validacao.Preco(preco);
        Nome = Validacao.Texto(nome, 120, "o nome");
        Preco = novoPreco;
    }
}

public enum TipoMaterial { Peca, Insumo }

public sealed class Material : Cadastro
{
    private Material() { }
    public Material(string? nome, TipoMaterial tipo, decimal preco)
    {
        if (!Enum.IsDefined(tipo)) throw new EntradaInvalida("Tipo de material inválido.");
        Tipo = tipo;
        Atualizar(nome, preco);
    }

    public string Nome { get; private set; } = "";
    public TipoMaterial Tipo { get; private set; }
    public decimal Preco { get; private set; }
    public int Saldo { get; private set; }
    public int Reservado { get; private set; }
    public int Disponivel => Saldo - Reservado;
    public Guid Versao { get; private set; } = Guid.NewGuid();

    public void Atualizar(string? nome, decimal preco)
    {
        var novoPreco = Validacao.Preco(preco);
        Nome = Validacao.Texto(nome, 120, "o nome");
        Preco = novoPreco;
        Versao = Guid.NewGuid();
    }

    public void AjustarSaldo(int quantidade)
    {
        var novoSaldo = (long)Saldo + quantidade;
        if (quantidade == 0 || novoSaldo > int.MaxValue)
            throw new EntradaInvalida("A movimentação de estoque é inválida.");
        if (novoSaldo < Reservado) throw new RegraViolada("O saldo não pode ficar abaixo da reserva.");
        Saldo = (int)novoSaldo;
        Versao = Guid.NewGuid();
    }

    public void Reservar(int quantidade)
    {
        if (!Ativo || quantidade <= 0 || quantidade > Disponivel)
            throw new RegraViolada($"Estoque indisponível para {Nome}.");
        Reservado += quantidade;
        Versao = Guid.NewGuid();
    }

    public void BaixarReserva(int quantidade)
    {
        if (quantidade <= 0 || quantidade > Reservado)
            throw new RegraViolada($"Reserva inválida para {Nome}.");
        Reservado -= quantidade;
        Saldo -= quantidade;
        Versao = Guid.NewGuid();
    }

    public override void Desativar()
    {
        if (Reservado > 0) throw new RegraViolada("O material possui estoque reservado.");
        base.Desativar();
        Versao = Guid.NewGuid();
    }
}
