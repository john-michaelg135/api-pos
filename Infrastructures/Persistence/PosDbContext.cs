using Microsoft.EntityFrameworkCore;
using Domains.Entities;

namespace Infrastructures.Persistence;

public class PosDbContext : DbContext
{
    public PosDbContext(DbContextOptions<PosDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Product ──
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.ProductId);
            entity.Property(e => e.ProductId).UseIdentityByDefaultColumn();
            entity.Property(e => e.ProductName).IsRequired().HasMaxLength(150);
            entity.Property(e => e.ProductCategory).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ProductDescription).HasColumnType("text");
            entity.Property(e => e.ProductImage).HasMaxLength(255);
            entity.Property(e => e.ScmsProductId).HasMaxLength(100);
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);

            entity.HasMany(e => e.Variations)
                .WithOne(v => v.Product)
                .HasForeignKey(v => v.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ProductVariation ──
        modelBuilder.Entity<ProductVariation>(entity =>
        {
            entity.HasKey(e => e.VariationId);
            entity.Property(e => e.VariationId).UseIdentityByDefaultColumn();
            entity.Property(e => e.VariationName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);

            entity.HasMany(e => e.ProductPrices)
                .WithOne(p => p.ProductVariation)
                .HasForeignKey(p => p.VariationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.PriceHistories)
                .WithOne(h => h.ProductVariation)
                .HasForeignKey(h => h.VariationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.OrderItems)
                .WithOne(oi => oi.ProductVariation)
                .HasForeignKey(oi => oi.VariationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── ProductPrice (current active prices) ──
        modelBuilder.Entity<ProductPrice>(entity =>
        {
            entity.HasKey(e => e.PriceId);
            entity.Property(e => e.PriceId).UseIdentityByDefaultColumn();
            entity.Property(e => e.Price).IsRequired().HasColumnType("numeric(10,2)");
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.EffectiveFrom).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // SetBy references users in auth_db — stored as plain int, no FK constraint
        });

        // ── PriceHistory (archived prices) ──
        modelBuilder.Entity<PriceHistory>(entity =>
        {
            entity.HasKey(e => e.HistoryId);
            entity.Property(e => e.HistoryId).UseIdentityByDefaultColumn();
            entity.Property(e => e.Price).IsRequired().HasColumnType("numeric(10,2)");
            entity.Property(e => e.EffectiveFrom).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // SetBy references users in auth_db — stored as plain int, no FK constraint
        });

        // ── Location ──
        modelBuilder.Entity<Location>(entity =>
        {
            entity.HasKey(e => e.LocationId);
            entity.Property(e => e.LocationId).UseIdentityByDefaultColumn();
            entity.Property(e => e.LocationName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LocationType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasMany(e => e.Orders)
                .WithOne(o => o.Location)
                .HasForeignKey(o => o.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Order ──
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.OrderId).UseIdentityByDefaultColumn();
            entity.Property(e => e.OrderNumber).IsRequired().HasMaxLength(30);
            entity.Property(e => e.OrderType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.OrderSource).IsRequired().HasMaxLength(30);
            entity.Property(e => e.DeliveryAddress).HasColumnType("text");
            
            // Institutional Normalized Address Fields
            entity.Property(e => e.InstitutionalStreet).HasMaxLength(255);
            entity.Property(e => e.InstitutionalCity).HasMaxLength(100);
            entity.Property(e => e.InstitutionalProvince).HasMaxLength(100);
            entity.Property(e => e.InstitutionalZipCode).HasMaxLength(20);

            entity.Property(e => e.ContactPerson).HasMaxLength(150);
            entity.Property(e => e.IsPreorder).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.CustomVariationNotes).HasColumnType("text");

            // Dedicated Senior/PWD Fields Configuration
            entity.Property(e => e.SeniorPwdId).HasMaxLength(30);
            entity.Property(e => e.SeniorPwdName).HasMaxLength(150);
            entity.Property(e => e.SeniorPwdStreet).HasMaxLength(255);
            entity.Property(e => e.SeniorPwdBarangay).HasMaxLength(100);
            entity.Property(e => e.SeniorPwdCity).HasMaxLength(100);
            entity.Property(e => e.SeniorPwdProvince).HasMaxLength(100);
            entity.Property(e => e.SeniorPwdZipCode).HasMaxLength(20);
            entity.Property(e => e.PaymentMethod).IsRequired().HasMaxLength(20);
            entity.Property(e => e.PaymentStatus).IsRequired().HasMaxLength(20);
            entity.Property(e => e.OrderStatus).IsRequired().HasMaxLength(30);
            entity.Property(e => e.TotalAmount).IsRequired().HasColumnType("numeric(12,2)");
            entity.Property(e => e.RejectionRemarks).HasColumnType("text");
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // SubmittedBy, ApprovedBy reference users in auth_db — stored as plain int, no FK constraint

            entity.HasIndex(e => e.OrderNumber).IsUnique();

            entity.HasMany(e => e.OrderItems)
                .WithOne(oi => oi.Order)
                .HasForeignKey(oi => oi.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Payments)
                .WithOne(p => p.Order)
                .HasForeignKey(p => p.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Payment ──
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.PaymentId);
            entity.Property(e => e.PaymentId).UseIdentityByDefaultColumn();
            entity.Property(e => e.AmountPaid).IsRequired().HasColumnType("numeric(12,2)");
            entity.Property(e => e.PaymentChannel).IsRequired().HasMaxLength(50);
            entity.Property(e => e.GatewayReferenceNumber).HasMaxLength(100);
            entity.Property(e => e.PaymentStatus).IsRequired().HasMaxLength(30);
            entity.Property(e => e.PaidAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // ── OrderItem ──
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasKey(e => e.ItemId);
            entity.Property(e => e.ItemId).UseIdentityByDefaultColumn();
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.UnitPrice).IsRequired().HasColumnType("numeric(10,2)");
            entity.Property(e => e.Subtotal).IsRequired().HasColumnType("numeric(12,2)");
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // ── Stock (current quantity per variation per location) ──
        modelBuilder.Entity<Stock>(entity =>
        {
            entity.HasKey(e => e.StockId);
            entity.Property(e => e.StockId).UseIdentityByDefaultColumn();
            entity.Property(e => e.Quantity).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.MinThreshold).IsRequired().HasDefaultValue(0);
            entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Unique: one record per variation per location
            entity.HasIndex(e => new { e.VariationId, e.LocationId }).IsUnique();

            entity.HasOne(e => e.Variation)
                .WithMany()
                .HasForeignKey(e => e.VariationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => e.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── RefundRequest (Sprint 3 — US-POS-016/017) ──
        modelBuilder.Entity<RefundRequest>(entity =>
        {
            entity.HasKey(e => e.RefundRequestId);
            entity.Property(e => e.RefundRequestId).UseIdentityByDefaultColumn();
            entity.Property(e => e.Reason).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(30).HasDefaultValue("Pending");
            entity.Property(e => e.QuantityToReturn).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // RequestedBy, ApprovedBy reference users in auth_db — stored as plain int, no FK constraint

            entity.HasOne(e => e.Variation)
                .WithMany()
                .HasForeignKey(e => e.VariationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── StockAdjustment (Sprint 3 — US-POS-019/020) ──
        modelBuilder.Entity<StockAdjustment>(entity =>
        {
            entity.HasKey(e => e.AdjustmentId);
            entity.Property(e => e.AdjustmentId).UseIdentityByDefaultColumn();
            entity.Property(e => e.AdjustmentType).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.Reason).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(30).HasDefaultValue("PendingApproval");
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // SubmittedBy, ApprovedBy reference users in auth_db — stored as plain int, no FK constraint

            entity.HasOne(e => e.Variation)
                .WithMany()
                .HasForeignKey(e => e.VariationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => e.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── CartItem (Sprint 3 — CRMS read endpoint) ──
        modelBuilder.Entity<CartItem>(entity =>
        {
            entity.HasKey(e => e.CartItemId);
            entity.Property(e => e.CartItemId).UseIdentityByDefaultColumn();
            entity.Property(e => e.CustomerId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Quantity).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

            // One cart item per customer per variation
            entity.HasIndex(e => new { e.CustomerId, e.VariationId }).IsUnique();

            entity.HasOne(e => e.Variation)
                .WithMany()
                .HasForeignKey(e => e.VariationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── StockReceiving (audit log of stock deliveries) ──
        modelBuilder.Entity<StockReceiving>(entity =>
        {
            entity.HasKey(e => e.ReceivingId);
            entity.Property(e => e.ReceivingId).UseIdentityByDefaultColumn();
            entity.Property(e => e.QuantityReceived).IsRequired();
            entity.Property(e => e.Notes).HasColumnType("text");
            entity.Property(e => e.ReceivedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            // ReceivedBy references users in auth_db — stored as plain int, no FK constraint

            entity.HasOne(e => e.Variation)
                .WithMany()
                .HasForeignKey(e => e.VariationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Location)
                .WithMany()
                .HasForeignKey(e => e.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        // ── StockTransfer (Integration with SCMS) ──
        modelBuilder.Entity<StockTransfer>(entity =>
        {
            entity.HasKey(e => e.TransferId);
            entity.Property(e => e.TransferId).IsRequired().HasMaxLength(50);
            entity.Property(e => e.SourceLocationId).HasMaxLength(50);
            entity.Property(e => e.SourceLocationName).HasMaxLength(100);
            entity.Property(e => e.DestinationBranchId).HasMaxLength(50);
            entity.Property(e => e.DestinationBranchName).HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(30);
            
            entity.HasMany(e => e.Items)
                .WithOne(i => i.Transfer)
                .HasForeignKey(i => i.TransferId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StockTransferItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityByDefaultColumn();
            entity.Property(e => e.ProductId).HasMaxLength(50);
            entity.Property(e => e.ProductName).HasMaxLength(150);
            entity.Property(e => e.Quantity).IsRequired();
        });

        modelBuilder.Entity<OrderStatusHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityByDefaultColumn();
            entity.Property(e => e.OldStatus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.NewStatus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Remarks).HasColumnType("text");
            entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.Order)
                .WithMany(o => o.StatusHistory)
                .HasForeignKey(e => e.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    public DbSet<Product> Products { get; set; }
    public DbSet<ProductVariation> ProductVariations { get; set; }
    public DbSet<ProductPrice> ProductPrices { get; set; }
    public DbSet<PriceHistory> PriceHistories { get; set; }
    public DbSet<Location> Locations { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<Stock> Stocks { get; set; }
    public DbSet<StockReceiving> StockReceivings { get; set; }
    public DbSet<StockTransfer> StockTransfers { get; set; }
    public DbSet<StockTransferItem> StockTransferItems { get; set; }

    // Sprint 3
    public DbSet<RefundRequest> RefundRequests { get; set; }
    public DbSet<StockAdjustment> StockAdjustments { get; set; }
    public DbSet<CartItem> CartItems { get; set; }
    public DbSet<OrderStatusHistory> OrderStatusHistories { get; set; }
}