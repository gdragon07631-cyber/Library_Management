using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Project.Data;

#nullable disable

namespace Project.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20261011120000_AddHoldPickupExpiryRuns")]
public partial class AddHoldPickupExpiryRuns
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

        modelBuilder.Entity("Project.Models.HoldPickupExpiryRun", entity =>
        {
            entity.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            SqlServerPropertyBuilderExtensions.UseIdentityColumn(entity.Property<long>("Id"));
            entity.Property<int>("AssignedToNextReaderCount").HasColumnType("int");
            entity.Property<int>("CancelledCount").HasColumnType("int");
            entity.Property<DateOnly>("BusinessDate").HasColumnType("date");
            entity.Property<int>("CheckedCount").HasColumnType("int");
            entity.Property<DateTime?>("CompletedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("ErrorMessage").HasMaxLength(2000).HasColumnType("nvarchar(2000)");
            entity.Property<int>("ReturnedToAvailableCount").HasColumnType("int");
            entity.Property<string>("ResultMessage").HasMaxLength(500).HasColumnType("nvarchar(500)");
            entity.Property<DateTime>("StartedAtUtc").HasColumnType("datetime2");
            entity.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("nvarchar(20)");
            entity.HasKey("Id");
            entity.HasIndex("BusinessDate").IsUnique();
            entity.HasIndex("StartedAtUtc");
            entity.ToTable("HoldPickupExpiryRuns");
        });
    }
}
