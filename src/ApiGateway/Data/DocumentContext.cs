using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace ApiGateway.Data;

public class DocumentContext : DbContext
{
    public DocumentContext(DbContextOptions<DocumentContext> options) : base(options)
    {
    }

    public DbSet<Document> Documents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalFileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.UploadedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }
}