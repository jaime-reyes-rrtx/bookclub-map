using Microsoft.EntityFrameworkCore;

namespace RAG.Core.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var document = modelBuilder.Entity<DocumentRecord>();
        document.HasKey(x => x.Id);
        document.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        document.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        document.Property(x => x.ObjectKey).HasMaxLength(1024).IsRequired();
        document.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        document.Property(x => x.ErrorMessage).HasMaxLength(4000);
        document.HasIndex(x => x.Status);
        document.HasIndex(x => x.CreatedAtUtc);
    }
}
