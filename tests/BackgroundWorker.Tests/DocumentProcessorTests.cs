using BackgroundWorker.Data;
using BackgroundWorker.Services;
using iText.Kernel.Pdf;
using iText.Layout.Element;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using PdfDoc = iText.Layout.Document;
using SharedDoc = Shared.Models.Document;
using DocumentStatus = Shared.Models.DocumentStatus;

namespace BackgroundWorker.Tests;

public class DocumentProcessorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private DocumentProcessor BuildProcessor(out DocumentContext context)
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        context = new DocumentContext(options);

        var capturedContext = context;
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider.GetService(typeof(DocumentContext))).Returns(capturedContext);

        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new DocumentProcessor(
            new Mock<IConnection>().Object,
            scopeFactory.Object,
            NullLogger<DocumentProcessor>.Instance);
    }

    private string CreateTempPdf(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        _tempFiles.Add(path);

        using var writer = new PdfWriter(path);
        using var pdfDoc = new PdfDocument(writer);
        using var doc = new PdfDoc(pdfDoc);
        doc.Add(new Paragraph(text));

        return path;
    }

    private string CreateTempCorruptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        _tempFiles.Add(path);
        File.WriteAllText(path, "this is not a valid pdf");
        return path;
    }

    private static SharedDoc SeedDocument(DocumentContext context, string filePath, DocumentStatus status = DocumentStatus.Uploaded)
    {
        var doc = new SharedDoc
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test.pdf",
            FilePath = filePath,
            Status = status
        };
        context.Documents.Add(doc);
        context.SaveChanges();
        return doc;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessDocumentAsync_WhenDocumentNotFoundInDb_ReturnsWithoutChanges()
    {
        // Arrange
        var processor = BuildProcessor(out var context);
        var nonExistentId = Guid.NewGuid();

        // Act
        await processor.ProcessDocumentAsync(nonExistentId, CancellationToken.None);

        // Assert
        Assert.Equal(0, await context.Documents.CountAsync());
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenPdfFileIsMissing_SetsStatusToFailed()
    {
        // Arrange
        var processor = BuildProcessor(out var context);
        var missingFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        var doc = SeedDocument(context, missingFilePath);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() =>
            processor.ProcessDocumentAsync(doc.Id, CancellationToken.None));

        // Assert
        var updated = await context.Documents.FindAsync(doc.Id);
        Assert.Equal(DocumentStatus.Failed, updated!.Status);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenPdfIsValid_SetsStatusToCompletedAndExtractsText()
    {
        // Arrange
        var processor = BuildProcessor(out var context);
        var expectedText = "Hello from iText7 test PDF";
        var pdfPath = CreateTempPdf(expectedText);
        var doc = SeedDocument(context, pdfPath);
        var beforeProcessing = DateTimeOffset.UtcNow;

        // Act
        await processor.ProcessDocumentAsync(doc.Id, CancellationToken.None);

        // Assert
        var updated = await context.Documents.FindAsync(doc.Id);
        Assert.Equal(DocumentStatus.Completed, updated!.Status);
        Assert.Contains(expectedText, updated.ExtractedText);
        Assert.True(updated.ProcessedAt >= beforeProcessing);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenPdfIsCorrupt_SetsStatusToFailedAndRethrows()
    {
        // Arrange
        var processor = BuildProcessor(out var context);
        var corruptPath = CreateTempCorruptFile();
        var doc = SeedDocument(context, corruptPath);

        // Act
        await Assert.ThrowsAnyAsync<Exception>(() =>
            processor.ProcessDocumentAsync(doc.Id, CancellationToken.None));

        // Assert
        var updated = await context.Documents.FindAsync(doc.Id);
        Assert.Equal(DocumentStatus.Failed, updated!.Status);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
            File.Delete(file);
    }
}
