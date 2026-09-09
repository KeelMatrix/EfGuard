using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef9SqlServerFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();
}

public sealed class Order
{
    public int Id { get; set; }
}

[Migration("20240203000000_AddIndex")]
public sealed class AddIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_Orders_Id", "Orders", ["Id"]);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_Orders_Id", "Orders");
}
