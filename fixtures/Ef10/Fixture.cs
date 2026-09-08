using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef10Fixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();
}

public sealed class Order
{
    public int Id { get; set; }
}

[Migration("20240303000000_AddRequiredColumn")]
public sealed class AddRequiredColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>("Code", "Orders", nullable: false);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("Code", "Orders");
}
