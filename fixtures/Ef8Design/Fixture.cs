using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8DesignFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();
}

public sealed class Order
{
    public int Id { get; set; }
    public string? LegacyCode { get; set; }
}

[DbContext(typeof(OrdersDbContext))]
[Migration("20240103000000_DropDesignLegacyCode")]
public sealed class DropDesignLegacyCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("LegacyCode", "Orders");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("LegacyCode", "Orders", nullable: true);
}
