using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.Ef9NarrowPostgresFixture;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Order>(entity => entity.Property(order => order.Code).HasMaxLength(32));
}

public sealed class Order
{
    public int Id { get; set; }
    public string? Code { get; set; }
}

[DbContext(typeof(OrdersDbContext))]
[Migration("20240802000000_NarrowOrdersCode")]
public sealed class NarrowOrdersCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
        name: "Code",
        table: "Orders",
        type: "character varying(32)",
        maxLength: 32,
        nullable: true,
        oldClrType: typeof(string),
        oldType: "text",
        oldNullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>(
        name: "Code",
        table: "Orders",
        type: "text",
        nullable: true,
        oldClrType: typeof(string),
        oldType: "character varying(32)",
        oldMaxLength: 32,
        oldNullable: true);
}
