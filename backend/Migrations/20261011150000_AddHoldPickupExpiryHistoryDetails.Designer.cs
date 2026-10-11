using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261011150000_AddHoldPickupExpiryHistoryDetails")]
public partial class AddHoldPickupExpiryHistoryDetails
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);
        modelBuilder.Entity("Project.Models.BookHold", entity =>
        {
            entity.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<long>("Id"));
            entity.Property<long?>("ExpiredBookCopyId").HasColumnType("bigint");
            entity.Property<string>("ExpiredBookCopyCode").HasMaxLength(50).HasColumnType("nvarchar(50)");
            entity.Property<long?>("ExpiryNextHoldId").HasColumnType("bigint");
            entity.Property<string>("ExpiryCopyOutcome").HasMaxLength(30).HasColumnType("nvarchar(30)");
            entity.HasKey("Id");
            entity.HasIndex("ExpiredBookCopyId");
            entity.ToTable("BookHolds");
        });
    }
}
