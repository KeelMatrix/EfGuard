using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8UnattributedMigrations;

public sealed class UnattributedDbContext(DbContextOptions<UnattributedDbContext> options) : DbContext(options)
{
    public DbSet<UnattributedRecord> Records => Set<UnattributedRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<UnattributedRecord>(entity => entity.ToTable("UnattributedRecords"));
}

public sealed class UnattributedRecord
{
    public int Id { get; set; }
    public string? Legacy { get; set; }
}

public sealed class UnattributedDbContextFactory : IDesignTimeDbContextFactory<UnattributedDbContext>
{
    public UnattributedDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<UnattributedDbContext> options = new DbContextOptionsBuilder<UnattributedDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardUnattributed;Trusted_Connection=True")
            .Options;
        return new UnattributedDbContext(options);
    }
}

// EF Core attributes a migration to a DbContext through [DbContext(typeof(...))]. This migration intentionally
// omits that attribute so EfGuard's fail-closed behavior for unattributed migration classes stays covered.
[Migration("20250104000000_UnattributedDropLegacy")]
public sealed class UnattributedDropLegacy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Legacy", "UnattributedRecords");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("Legacy", "UnattributedRecords", nullable: true);
}
