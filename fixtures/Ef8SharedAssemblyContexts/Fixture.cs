using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8SharedAssemblyContexts;

public sealed class AlphaDbContext(DbContextOptions<AlphaDbContext> options) : DbContext(options)
{
    public DbSet<AlphaRecord> Records => Set<AlphaRecord>();
}

public sealed class BetaDbContext(DbContextOptions<BetaDbContext> options) : DbContext(options)
{
    public DbSet<BetaCustomer> Customers => Set<BetaCustomer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<BetaCustomer>(entity => entity.ToTable("BetaCustomers"));
}

public sealed class GammaDbContext(DbContextOptions<GammaDbContext> options) : DbContext(options)
{
    public DbSet<GammaInvoice> Invoices => Set<GammaInvoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<GammaInvoice>(entity => entity.ToTable("GammaInvoices"));
}

public sealed class AlphaRecord
{
    public int Id { get; set; }
}

public sealed class BetaCustomer
{
    public int Id { get; set; }
    public string? BetaLegacy { get; set; }
}

public sealed class GammaInvoice
{
    public int Id { get; set; }
    public string? Reference { get; set; }
}

public sealed class AlphaDbContextFactory : IDesignTimeDbContextFactory<AlphaDbContext>
{
    public AlphaDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<AlphaDbContext> options = new DbContextOptionsBuilder<AlphaDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardSharedAssemblyAlpha;Trusted_Connection=True")
            .Options;
        return new AlphaDbContext(options);
    }
}

public sealed class BetaDbContextFactory : IDesignTimeDbContextFactory<BetaDbContext>
{
    public BetaDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<BetaDbContext> options = new DbContextOptionsBuilder<BetaDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardSharedAssemblyBeta;Trusted_Connection=True")
            .Options;
        return new BetaDbContext(options);
    }
}

public sealed class GammaDbContextFactory : IDesignTimeDbContextFactory<GammaDbContext>
{
    public GammaDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<GammaDbContext> options = new DbContextOptionsBuilder<GammaDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardSharedAssemblyGamma;Trusted_Connection=True")
            .Options;
        return new GammaDbContext(options);
    }
}

[DbContext(typeof(BetaDbContext))]
[Migration("20250102000000_BetaDropLegacy")]
public sealed class BetaDropLegacy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("BetaLegacy", "BetaCustomers");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("BetaLegacy", "BetaCustomers", nullable: true);
}

[DbContext(typeof(GammaDbContext))]
[Migration("20250103000000_GammaAddInvoiceReferenceIndex")]
public sealed class GammaAddInvoiceReferenceIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_GammaInvoices_Reference", "GammaInvoices", ["Reference"]);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_GammaInvoices_Reference", "GammaInvoices");
}
