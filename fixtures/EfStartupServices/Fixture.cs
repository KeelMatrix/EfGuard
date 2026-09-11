using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.EfStartupServicesFixture;

public sealed class StartupServicesDbContext(DbContextOptions<StartupServicesDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public sealed class Order
{
    public int Id { get; set; }
    public string? Code { get; set; }
}

[Migration("20240511000000_AddStartupServicesIndex")]
public sealed class AddStartupServicesIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateIndex("IX_Orders_Code", "Orders", ["Code"]);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropIndex("IX_Orders_Code", "Orders");
}
