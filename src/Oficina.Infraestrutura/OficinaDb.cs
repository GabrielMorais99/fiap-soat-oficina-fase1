using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oficina.Aplicacao;
using Oficina.Dominio;

namespace Oficina.Infraestrutura;

public sealed class OficinaDb(DbContextOptions<OficinaDb> options) : DbContext(options), IDadosOrdens
{
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Veiculo> Veiculos => Set<Veiculo>();
    public DbSet<Servico> Servicos => Set<Servico>();
    public DbSet<Material> Materiais => Set<Material>();
    public DbSet<OrdemServico> Ordens => Set<OrdemServico>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Cliente>(e =>
        {
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.Documento).HasConversion(x => x.Valor, x => new Documento(x)).HasMaxLength(14);
            e.HasIndex(x => x.Documento).IsUnique();
        });
        m.Entity<Veiculo>(e =>
        {
            e.Property(x => x.Placa).HasConversion(x => x.Valor, x => new Placa(x)).HasMaxLength(7);
            e.Property(x => x.Marca).HasMaxLength(60);
            e.Property(x => x.Modelo).HasMaxLength(80);
            e.HasIndex(x => x.Placa).IsUnique();
            e.HasOne<Cliente>().WithMany().HasForeignKey(x => x.ClienteId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Servico>(e =>
        {
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.Preco).HasPrecision(8, 2);
        });
        m.Entity<Material>(e =>
        {
            e.Property(x => x.Nome).HasMaxLength(120);
            e.Property(x => x.Preco).HasPrecision(8, 2);
            e.Property(x => x.Versao).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("CK_Materiais_Saldo",
                "\"Saldo\" >= \"Reservado\" AND \"Reservado\" >= 0"));
        });
        m.Entity<OrdemServico>(e =>
        {
            e.Property(x => x.Versao).IsConcurrencyToken();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Orcamentos).HasConversion(x => Serializar(x), x => Desserializar<Orcamento[]>(x))
                .HasColumnType("jsonb");
            e.Property(x => x.Historico).HasConversion(x => Serializar(x), x => Desserializar<EventoOrdem[]>(x))
                .HasColumnType("jsonb");
            e.HasIndex(x => new { x.Status, x.CriadaEm });
            e.HasIndex(x => x.CriadaEm);
            e.HasOne<Cliente>().WithMany().HasForeignKey(x => x.ClienteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Veiculo>().WithMany().HasForeignKey(x => x.VeiculoId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public Task<Cliente?> BuscarCliente(Documento documento, CancellationToken ct) =>
        Clientes.SingleOrDefaultAsync(x => x.Ativo && x.Documento == documento, ct);

    public Task<Veiculo?> BuscarVeiculo(Placa placa, CancellationToken ct) =>
        Veiculos.SingleOrDefaultAsync(x => x.Ativo && x.Placa == placa, ct);

    public Task<Servico[]> BuscarServicos(Guid[] ids, CancellationToken ct) =>
        Servicos.Where(x => x.Ativo && ids.Contains(x.Id)).ToArrayAsync(ct);

    public Task<Material[]> BuscarMateriais(Guid[] ids, CancellationToken ct) =>
        Materiais.Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(ct);

    public Task<OrdemServico?> BuscarOrdem(Guid id, CancellationToken ct) =>
        Ordens.SingleOrDefaultAsync(x => x.Id == id, ct);

    public void Adicionar(OrdemServico ordem) => Ordens.Add(ordem);
    public async Task Salvar(CancellationToken ct) => await SaveChangesAsync(ct);

    private static string Serializar<T>(T valor) => JsonSerializer.Serialize(valor);
    private static T Desserializar<T>(string valor) => JsonSerializer.Deserialize<T>(valor)!;
}
