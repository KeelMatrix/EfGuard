using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8Fixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.LegacyCode).HasMaxLength(32);
        });
    }
}

public sealed class Order
{
    public int Id { get; set; }
    public string? LegacyCode { get; set; }
}

[Migration("20240101000000_RemoveLegacyCode")]
public sealed class RemoveLegacyCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("LegacyCode", "Orders");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("LegacyCode", "Orders", nullable: true, maxLength: 32);
}
