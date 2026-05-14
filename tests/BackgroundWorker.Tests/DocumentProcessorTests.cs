using System.Reflection;
using BackgroundWorker.Data;
using BackgroundWorker.Services;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.EntityFrameworkCore;
using Shared.Models;
using SharedDocument = Shared.Models.Document;
using PdfLayoutDocument = iText.Layout.Document;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace BackgroundWorker.Tests;

public class DocumentProcessorTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    // Helpers

    private static DocumentContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new DocumentContext(options);
    }

    private static (DocumentProcessor processor, DocumentContext context) BuildProcessor(string dbName)
    {
        var context = CreateInMemoryContext(dbName);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider.GetService(typeof(DocumentContext))).Returns(context);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var rabbitConnection = new Mock<IConnection>();
        var logger = NullLogger<DocumentProcessor>.Instance;

        var processor = new DocumentProcessor(rabbitConnection.Object, scopeFactory.Object, logger);
        return (processor, context);
    }

    private static Task InvokeProcessDocumentAsync(DocumentProcessor processor, Guid documentId, CancellationToken ct = default)
    {
        var method = typeof(DocumentProcessor).GetMethod(
            "ProcessDocumentAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
            throw new InvalidOperationException("ProcessDocumentAsync method not found via reflection.");

        return (Task)method.Invoke(processor, new object[] { documentId, ct })!;
    }

    private string CreateTempPdfWithText(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        _tempFiles.Add(path);

        using var writer = new PdfWriter(path);
        using var pdfDoc = new PdfDocument(writer);
        using var document = new PdfLayoutDocument(pdfDoc);
        document.Add(new Paragraph(text));

        return path;
    }

    private string GetNonExistentTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        // Do NOT create the file; we want it absent.
        return path;
    }

    // Tests

    [Fact]
    public async Task ProcessDocumentAsync_DocumentNotFound_ReturnsEarlyWithoutStatusChange()
    {
        var (processor, context) = BuildProcessor(nameof(ProcessDocumentAsync_DocumentNotFound_ReturnsEarlyWithoutStatusChange));
        var missingId = Guid.NewGuid();

        // Should complete without throwing
        await InvokeProcessDocumentAsync(processor, missingId);

        // DB still has no documents
        var count = await context.Documents.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProcessDocumentAsync_PdfFileDoesNotExist_SetsStatusToFailed()
    {
        var (processor, context) = BuildProcessor(nameof(ProcessDocumentAsync_PdfFileDoesNotExist_SetsStatusToFailed));

        var document = new SharedDocument
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test.pdf",
            FilePath = GetNonExistentTempPath(),
            Status = DocumentStatus.Uploaded
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        // Should throw (FileNotFoundException is rethrown)
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeProcessDocumentAsync(processor, document.Id));

        var updated = await context.Documents.FindAsync(document.Id);
        Assert.NotNull(updated);
        Assert.Equal(DocumentStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task ProcessDocumentAsync_ValidPdf_SetsStatusToCompletedAndPopulatesText()
    {
        var (processor, context) = BuildProcessor(nameof(ProcessDocumentAsync_ValidPdf_SetsStatusToCompletedAndPopulatesText));

        var expectedText = "Hello from iText7 test PDF";
        var pdfPath = CreateTempPdfWithText(expectedText);

        var document = new SharedDocument
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test.pdf",
            FilePath = pdfPath,
            Status = DocumentStatus.Uploaded
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        await InvokeProcessDocumentAsync(processor, document.Id);

        var updated = await context.Documents.FindAsync(document.Id);
        Assert.NotNull(updated);
        Assert.Equal(DocumentStatus.Completed, updated!.Status);
        Assert.NotNull(updated.ExtractedText);
        Assert.Contains(expectedText, updated.ExtractedText);
        Assert.NotNull(updated.ProcessedAt);
        Assert.True(updated.ProcessedAt >= before);
    }

    [Fact]
    public async Task ProcessDocumentAsync_ExceptionDuringProcessing_SetsStatusToFailedAndRethrows()
    {
        var (processor, context) = BuildProcessor(nameof(ProcessDocumentAsync_ExceptionDuringProcessing_SetsStatusToFailedAndRethrows));

        // Point to a file that exists but is not a valid PDF (so iText7 will throw)
        var invalidPdfPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        _tempFiles.Add(invalidPdfPath);
        await File.WriteAllTextAsync(invalidPdfPath, "this is not a pdf");

        var document = new SharedDocument
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "corrupt.pdf",
            FilePath = invalidPdfPath,
            Status = DocumentStatus.Uploaded
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => InvokeProcessDocumentAsync(processor, document.Id));

        var updated = await context.Documents.FindAsync(document.Id);
        Assert.NotNull(updated);
        Assert.Equal(DocumentStatus.Failed, updated!.Status);
    }
}
