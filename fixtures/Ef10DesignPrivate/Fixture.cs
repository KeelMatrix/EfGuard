using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef10DesignPrivateFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();
}

public sealed class Order
{
    public int Id { get; set; }
}

[DbContext(typeof(OrdersDbContext))]
[Migration("20240306000000_AddDesignPrivateIndex")]
public sealed class AddDesignPrivateIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_Orders_Id", "Orders", ["Id"]);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_Orders_Id", "Orders");
}
