using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef9Net9Fixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql();
}

public sealed class Order
{
    public int Id { get; set; }
}

[Migration("20240204000000_AddNet9Index")]
public sealed class AddNet9Index : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_Orders_Id", "Orders", ["Id"], unique: true);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_Orders_Id", "Orders");
}
