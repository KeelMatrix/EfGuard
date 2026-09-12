using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef9DesignAppFixture;

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
[Migration("20240206000000_DropStartupDesignLegacyCode")]
public sealed class DropStartupDesignLegacyCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("LegacyCode", "Orders");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("LegacyCode", "Orders", nullable: true);
}
