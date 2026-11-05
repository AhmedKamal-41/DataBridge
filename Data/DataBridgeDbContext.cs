using DataBridge.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataBridge.Data;

public class DataBridgeDbContext : IdentityDbContext<IdentityUser>
{
    public DataBridgeDbContext(DbContextOptions<DataBridgeDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<ImportIssue> ImportIssues => Set<ImportIssue>();
    public DbSet<ImportAuditEntry> ImportAuditEntries => Set<ImportAuditEntry>();
    public DbSet<ImportBatchJob> ImportBatchJobs => Set<ImportBatchJob>();
    public DbSet<ImportBatchFile> ImportBatchFiles => Set<ImportBatchFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configures the AspNet* Identity tables.
        base.OnModelCreating(modelBuilder);

        ConfigureCustomer(modelBuilder);
        ConfigureImportBatch(modelBuilder);
        ConfigureImportRow(modelBuilder);
        ConfigureImportIssue(modelBuilder);
        ConfigureImportAuditEntry(modelBuilder);
        ConfigureImportBatchJob(modelBuilder);
        ConfigureImportBatchFile(modelBuilder);
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("Customers");

            customer.Property(c => c.Name).IsRequired().HasMaxLength(Customer.NameMaxLength);
            customer.Property(c => c.Company).HasMaxLength(Customer.CompanyMaxLength);
            customer.Property(c => c.Email).IsRequired().HasMaxLength(Customer.EmailMaxLength);
            customer.Property(c => c.NormalizedEmail).IsRequired().HasMaxLength(Customer.EmailMaxLength);
            customer.Property(c => c.Phone).HasMaxLength(Customer.PhoneMaxLength);
            customer.Property(c => c.Location).HasMaxLength(Customer.LocationMaxLength);
            customer.Property(c => c.Notes).HasMaxLength(Customer.NotesMaxLength);

            // Stored as text, such as "Active", so the table is easy to read.
            customer.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);

            customer.Property(c => c.RowVersion).IsRowVersion();

            customer.HasIndex(c => c.NormalizedEmail).IsUnique();
        });
    }

    private static void ConfigureImportBatch(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatch>(batch =>
        {
            batch.ToTable("ImportBatches");

            batch.Property(b => b.FileName).IsRequired().HasMaxLength(255);

            // No maximum length: a file can have any number of headers.
            batch.Property(b => b.OriginalHeadersJson).IsRequired();

            batch.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);

            batch.Property(b => b.RowVersion).IsRowVersion();

            // Deleting a batch also deletes its staged rows.
            batch.HasMany(b => b.Rows)
                .WithOne(r => r.Batch)
                .HasForeignKey(r => r.ImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImportRow(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportRow>(row =>
        {
            row.ToTable("ImportRows");

            // No maximum length: the cells are kept exactly as uploaded.
            row.Property(r => r.OriginalCellsJson).IsRequired();

            // The Draft columns also have no maximum length. A value that is too
            // long must be stored so that validation can flag it for a reviewer.

            row.Property(r => r.ReviewDecision).HasConversion<string>().HasMaxLength(20);
            row.Property(r => r.MatchResult).HasConversion<string>().HasMaxLength(20);

            row.Property(r => r.DecisionReason).HasMaxLength(500);

            // A SQL Server rowversion is always 8 bytes.
            row.Property(r => r.ApprovedCustomerVersion).HasMaxLength(8);

            // A customer that a staged row points to cannot be deleted.
            row.HasOne(r => r.MatchedCustomer)
                .WithMany()
                .HasForeignKey(r => r.MatchedCustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            // A source row can appear only once in a batch.
            row.HasIndex(r => new { r.ImportBatchId, r.SourceRowNumber }).IsUnique();
        });
    }

    private static void ConfigureImportIssue(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportIssue>(issue =>
        {
            issue.ToTable("ImportIssues");

            issue.Property(i => i.FieldName).IsRequired().HasMaxLength(50);
            issue.Property(i => i.Message).IsRequired().HasMaxLength(500);

            // Deleting a row also deletes its issues.
            issue.HasOne(i => i.Row)
                .WithMany(r => r.Issues)
                .HasForeignKey(i => i.ImportRowId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImportBatchJob(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatchJob>(job =>
        {
            job.ToTable("ImportBatchJobs");

            // One row per batch, replaced each time new work is requested.
            job.HasKey(j => j.ImportBatchId);

            job.Property(j => j.Kind).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Stage).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Error).HasMaxLength(1000);

            job.HasOne<ImportBatch>()
                .WithOne(b => b.Job)
                .HasForeignKey<ImportBatchJob>(j => j.ImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImportBatchFile(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatchFile>(file =>
        {
            file.ToTable("ImportBatchFiles");

            file.HasKey(f => f.ImportBatchId);
            file.Property(f => f.Content).IsRequired();

            file.HasOne<ImportBatch>()
                .WithOne(b => b.File)
                .HasForeignKey<ImportBatchFile>(f => f.ImportBatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureImportAuditEntry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportAuditEntry>(entry =>
        {
            entry.ToTable("ImportAuditEntries");

            entry.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entry.Property(e => e.Actor).IsRequired().HasMaxLength(200);

            // The audit trail must survive, so anything it points to cannot be
            // deleted. ImportAuditEntry has only ID properties, which is why
            // HasOne and WithMany name no navigation property.
            entry.HasOne<ImportBatch>()
                .WithMany()
                .HasForeignKey(e => e.ImportBatchId)
                .OnDelete(DeleteBehavior.Restrict);

            entry.HasOne<ImportRow>()
                .WithMany()
                .HasForeignKey(e => e.ImportRowId)
                .OnDelete(DeleteBehavior.Restrict);

            entry.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(e => e.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
