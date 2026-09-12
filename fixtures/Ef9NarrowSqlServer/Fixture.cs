using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef9NarrowSqlServerFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Order>(entity => entity.Property(order => order.Code).HasMaxLength(32));
}

public sealed class Order
{
    public int Id { get; set; }
    public string? Code { get; set; }
}

[DbContext(typeof(OrdersDbContext))]
[Migration("20240801000000_NarrowOrdersCode")]
public sealed class NarrowOrdersCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
        name: "Code",
        table: "Orders",
        type: "nvarchar(32)",
        maxLength: 32,
        nullable: true,
        oldClrType: typeof(string),
        oldType: "nvarchar(max)",
        oldNullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
        name: "Code",
        table: "Orders",
        type: "nvarchar(max)",
        nullable: true,
        oldClrType: typeof(string),
        oldType: "nvarchar(32)",
        oldMaxLength: 32,
        oldNullable: true);
}
