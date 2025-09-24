using Microsoft.EntityFrameworkCore;
using dataAccess.Entities;

namespace dataAccess.Services
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        // --- DbSets ---
        public DbSet<Supplier> Suppliers { get; set; } = default!;
        public DbSet<ProductCategory> ProductCategories { get; set; } = default!;
        public DbSet<Product> Products { get; set; } = default!;
        public DbSet<Order> Orders { get; set; } = default!;
        public DbSet<OrderItem> OrderItems { get; set; } = default!;
        public DbSet<Expense> Expenses { get; set; } = default!;
        public DbSet<DefectiveItem> DefectiveItems { get; set; } = default!;

        // Read-only projection for future Sales reporting
        public DbSet<Sales> Sales { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // --- Relationships ---
            modelBuilder.Entity<Order>()
                .HasMany(o => o.OrderItems)
                .WithOne(oi => oi.Order)
                .HasForeignKey(oi => oi.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Product>()
                .HasMany(p => p.OrderItems)
                .WithOne(oi => oi.Product)
                .HasForeignKey(oi => oi.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Product>()
                .HasMany(p => p.DefectiveItems)
                .WithOne(di => di.Product)
                .HasForeignKey(di => di.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            // --- Decimal precision ---
            modelBuilder.Entity<OrderItem>()
                .Property(oi => oi.UnitPrice)
                .HasPrecision(18, 2);

            modelBuilder.Entity<OrderItem>()
                .Property(oi => oi.Subtotal)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Order>()
                .Property(o => o.TotalAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Expense>()
                .Property(e => e.Amount)
                .HasPrecision(18, 2);

            // --- Schema + table/view mappings (Postgres is case-sensitive when quoted) ---
            modelBuilder.HasDefaultSchema("public");

            // Map entities to lowercase table names
            modelBuilder.Entity<Product>().ToTable("products");
            modelBuilder.Entity<Supplier>().ToTable("suppliers");
            modelBuilder.Entity<ProductCategory>().ToTable("productcategory");
            modelBuilder.Entity<Order>().ToTable("orders");
            modelBuilder.Entity<OrderItem>().ToTable("order_items");
            modelBuilder.Entity<Expense>().ToTable("expenses");
            modelBuilder.Entity<DefectiveItem>().ToTable("defective_items");

            // --- Sales projection (read-only) ---
            // If this is a view named 'sales', map as a view and keyless.
            modelBuilder.Entity<Sales>().ToView("sales");
            modelBuilder.Entity<Sales>().HasNoKey();

            // Product: map PascalCase properties -> lowercase columns
            modelBuilder.Entity<Product>(e =>
            {
                e.ToTable("products");           // already added, keep it
                e.HasKey(p => p.ProductId);      // optional but good to be explicit

                e.Property(p => p.ProductId).HasColumnName("productid");
                e.Property(p => p.ProductName).HasColumnName("productname");
                e.Property(p => p.Description).HasColumnName("description");
                e.Property(p => p.SupplierId).HasColumnName("supplierid");
                e.Property(p => p.CreatedAt).HasColumnName("createdat");
                e.Property(p => p.UpdatedAt).HasColumnName("updatedat");
                e.Property(p => p.ImageUrl).HasColumnName("image_url");
                e.Property(p => p.UpdatedByUserId).HasColumnName("updatedbyuserid");
            });

            modelBuilder.Entity<Supplier>(e =>
            {
                e.ToTable("suppliers");
                e.HasKey(s => s.SupplierId);

                e.Property(s => s.SupplierId).HasColumnName("supplierid");
                e.Property(s => s.SupplierName).HasColumnName("suppliername");
                e.Property(s => s.ContactPerson).HasColumnName("contactperson");
                e.Property(s => s.PhoneNumber).HasColumnName("phonenumber");
                e.Property(s => s.SupplierEmail).HasColumnName("supplieremail");
                e.Property(s => s.Address).HasColumnName("address");
                e.Property(s => s.CreatedAt).HasColumnName("createdat");
                e.Property(s => s.UpdatedAt).HasColumnName("updatedat");
                e.Property(s => s.SupplierStatus).HasColumnName("supplierstatus");
                e.Property(x => x.DefectReturned)
                    .HasColumnName("defectreturned")
                    .IsRequired(false);
            });

            modelBuilder.Entity<ProductCategory>(e =>
            {
                e.ToTable("productcategory");
                e.HasKey(c => c.ProductCategoryId);

                e.Property(c => c.ProductCategoryId).HasColumnName("productcategoryid");
                e.Property(c => c.ProductId).HasColumnName("productid");
                e.Property(c => c.Price).HasColumnName("price");
                e.Property(c => c.Cost).HasColumnName("cost");
                e.Property(c => c.Color).HasColumnName("color");
                e.Property(c => c.AgeSize).HasColumnName("agesize");
                e.Property(c => c.CurrentStock).HasColumnName("currentstock");
                e.Property(c => c.ReorderPoint).HasColumnName("reorderpoint");
                e.Property(c => c.UpdatedStock).HasColumnName("updatedstock");

                e.Property(x => x.AgeSize).IsRequired(false);
                e.Property(x => x.Color).IsRequired(false);
            });

            // NOTE: If you later hit "column does not exist" (snake_case columns),
            // add .Property(...).HasColumnName("column_name") for those specific properties
            // OR enable UseSnakeCaseNamingConvention() when registering the DbContext.
        }
    }
}
