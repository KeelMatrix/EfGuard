using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfGuard.EfPeripheralPartialAssembly;

public sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlServer();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
        });
    }
}

public sealed class Order
{
    public int Id { get; set; }
}

[DbContext(typeof(OrdersDbContext))]
[Migration("20240101000000_AddOrder")]
public sealed class AddOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "Orders",
        columns: table => new
        {
            Id = table.Column<int>(type: "int", nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_Orders", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Orders");
}
