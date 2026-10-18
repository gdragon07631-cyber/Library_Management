using Microsoft.EntityFrameworkCore;
using Project.Models;

namespace Project.Data;

internal static class FeePaymentMapping
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeePayment>(entity =>
        {
            entity.HasIndex(p => p.RequestId).IsUnique();
            entity.Property(p => p.Amount).HasColumnType("decimal(18,2)");
            entity.Property(p => p.BalanceAfter).HasColumnType("decimal(18,2)");
            entity.Property(p => p.PaidAtUtc).HasColumnType("datetime2");
            entity.HasOne(p => p.ReaderAccount).WithMany().HasForeignKey(p => p.ReaderAccountId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(p => p.CollectedByAdminAccount).WithMany().HasForeignKey(p => p.CollectedByAdminAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FeePaymentAllocation>(entity =>
        {
            entity.Property(a => a.Amount).HasColumnType("decimal(18,2)");
            entity.HasIndex(a => new { a.FeePaymentId, a.ReaderFeeId }).IsUnique();
            entity.HasOne(a => a.FeePayment).WithMany(p => p.Allocations).HasForeignKey(a => a.FeePaymentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(a => a.ReaderFee).WithMany().HasForeignKey(a => a.ReaderFeeId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
