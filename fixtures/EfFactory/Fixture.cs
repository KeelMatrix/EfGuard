using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EfGuard.EfFactoryFixture;

public sealed class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();
}

public sealed class FactoryDbContextFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<FactoryDbContext> options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=EfGuardFixture;Trusted_Connection=True")
            .Options;
        return new FactoryDbContext(options);
    }
}

public sealed class Order
{
    public int Id { get; set; }
    public string? Code { get; set; }
}

public sealed class AuditEntry
{
    public int Id { get; set; }
}

[DbContext(typeof(FactoryDbContext))]
[Migration("20240505000000_AddFactoryIndex")]
public sealed class AddFactoryIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_Orders_Code", "Orders", ["Code"], unique: true);
        migrationBuilder.Sql("UPDATE [Orders] SET [Code] = 'generated' WHERE [Id] > 0");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex("IX_Orders_Code", "Orders");
}

[DbContext(typeof(FactoryDbContext))]
[Migration("20240506000000_ChangeFactoryCollation")]
public sealed class ChangeFactoryCollation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>("Code", "Orders", type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2", oldClrType: typeof(string), oldType: "nvarchar(max)", oldCollation: "Latin1_General_100_CI_AS");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.AlterColumn<string>("Code", "Orders", type: "nvarchar(max)", nullable: true, collation: "Latin1_General_100_CI_AS", oldClrType: typeof(string), oldType: "nvarchar(128)", oldCollation: "Latin1_General_100_BIN2");
}

[DbContext(typeof(FactoryDbContext))]
[Migration("20240507000000_AddFactoryConstraint")]
public sealed class AddFactoryConstraint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddUniqueConstraint("AK_Orders_Code", "Orders", ["Code"]);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropUniqueConstraint("AK_Orders_Code", "Orders");
}

[DbContext(typeof(FactoryDbContext))]
[Migration("20240508000000_BackfillFactoryCode")]
public sealed class BackfillFactoryCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("-- WHERE\nUPDATE [Orders] SET [Code] = 'generated'");
    protected override void Down(MigrationBuilder migrationBuilder) { }
}

[DbContext(typeof(FactoryDbContext))]
[Migration("20240509000000_CteFactoryCode")]
public sealed class CteFactoryCode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("WITH recent AS (SELECT [Id] FROM [Orders] WHERE [Id] > 0) UPDATE [Orders] SET [Code] = 'generated' FROM recent WHERE [Orders].[Id] = recent.[Id]");
    protected override void Down(MigrationBuilder migrationBuilder) { }
}

public sealed class MarkerOperation : MigrationOperation { }

[DbContext(typeof(FactoryDbContext))]
[Migration("20240510000000_CustomFactoryOperation")]
public sealed class CustomFactoryOperation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Operations.Add(new MarkerOperation());
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
