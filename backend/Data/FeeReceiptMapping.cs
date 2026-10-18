using Microsoft.EntityFrameworkCore;
using Project.Models;

namespace Project.Data;

internal static class FeeReceiptMapping
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeeReceipt>(entity =>
        {
            entity.HasKey(r => r.FeePaymentId);
            entity.Property(r => r.FeePaymentId).ValueGeneratedNever();
            entity.Property(r => r.ReaderName).HasMaxLength(100).IsRequired();
            entity.Property(r => r.CollectedByName).HasMaxLength(256).IsRequired();
            entity.HasOne(r => r.FeePayment).WithOne().HasForeignKey<FeeReceipt>(r => r.FeePaymentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FeeReceiptLine>(entity =>
        {
            entity.HasKey(r => r.FeePaymentAllocationId);
            entity.Property(r => r.FeePaymentAllocationId).ValueGeneratedNever();
            entity.Property(r => r.Description).HasMaxLength(500).IsRequired();
            entity.HasOne(r => r.FeePaymentAllocation).WithOne().HasForeignKey<FeeReceiptLine>(r => r.FeePaymentAllocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.FeeReceipt).WithMany(r => r.Lines).HasForeignKey(r => r.FeePaymentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
