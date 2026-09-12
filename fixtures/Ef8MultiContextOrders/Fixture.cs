using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef8MultiContextOrders;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public sealed class Order
{
    public int Id { get; set; }
    public string? Code { get; set; }
}

[Migration("20240701000000_AddOrdersCodeIndex")]
public sealed class AddOrdersCodeIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_Orders_Code", "Orders", ["Code"]);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_Orders_Code", "Orders");
}
