using ApiGateway.Data;
using ApiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using Shared.Models;

namespace ApiGateway.Tests;

public class DocumentServiceTests : IDisposable
{
    private readonly DocumentContext _context;
    private readonly Mock<IConnection> _rabbitMock;
    private readonly Mock<IChannel> _channelMock;
    private readonly string _tempUploadsPath;

    public DocumentServiceTests()
    {
        _context = new DocumentContext(
            new DbContextOptionsBuilder<DocumentContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        _channelMock = new Mock<IChannel>();
        _rabbitMock = new Mock<IConnection>();
        _rabbitMock
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_channelMock.Object);

        _tempUploadsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private DocumentService BuildService() =>
        new(_context, _rabbitMock.Object, Mock.Of<ILogger<DocumentService>>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Uploads:Path"] = _tempUploadsPath })
                .Build());

    private static Mock<IFormFile> BuildPdfFileMock(string fileName = "document.pdf", long size = 4)
    {
        var content = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF magic bytes
        var stream = new MemoryStream(content);
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.Length).Returns(size);
        mock.Setup(f => f.ContentType).Returns("application/pdf");
        mock.Setup(f => f.FileName).Returns(fileName);
        mock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((s, _) => stream.CopyTo(s))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private void SetupRabbitPublishSuccess()
    {
        _channelMock.Setup(c => c.QueueDeclareAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RabbitMQ.Client.QueueDeclareOk("document_processing_queue", 0, 0));
    }

    // -------------------------------------------------------------------------
    // GetDocumentsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocumentsAsync_WhenNoDocumentsExist_ReturnsEmptyArray()
    {
        // Arrange
        var service = BuildService();

        // Act
        var result = await service.GetDocumentsAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDocumentsAsync_WhenDocumentsExist_ReturnsAllDocuments()
    {
        // Arrange
        _context.Documents.AddRange(
            new Document { Id = Guid.NewGuid(), OriginalFileName = "a.pdf", FilePath = "/a.pdf" },
            new Document { Id = Guid.NewGuid(), OriginalFileName = "b.pdf", FilePath = "/b.pdf" });
        await _context.SaveChangesAsync();
        var service = BuildService();

        // Act
        var result = await service.GetDocumentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Length);
    }

    // -------------------------------------------------------------------------
    // GetDocumentAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocumentAsync_WhenDocumentDoesNotExist_ReturnsNull()
    {
        // Arrange
        var service = BuildService();

        // Act
        var result = await service.GetDocumentAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDocumentAsync_WhenDocumentExists_ReturnsCorrectDocument()
    {
        // Arrange
        var id = Guid.NewGuid();
        _context.Documents.Add(new Document { Id = id, OriginalFileName = "test.pdf", FilePath = "/test.pdf" });
        await _context.SaveChangesAsync();
        var service = BuildService();

        // Act
        var result = await service.GetDocumentAsync(id, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("test.pdf", result.OriginalFileName);
    }

    // -------------------------------------------------------------------------
    // UploadDocumentAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UploadDocumentAsync_WhenFileIsEmpty_ThrowsArgumentException()
    {
        // Arrange
        var emptyFile = BuildPdfFileMock(size: 0);
        var service = BuildService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadDocumentAsync(emptyFile.Object, CancellationToken.None));
    }

    [Fact]
    public async Task UploadDocumentAsync_WhenFileIsNotPdf_ThrowsArgumentException()
    {
        // Arrange
        var nonPdf = new Mock<IFormFile>();
        nonPdf.Setup(f => f.Length).Returns(100);
        nonPdf.Setup(f => f.ContentType).Returns("image/png");
        nonPdf.Setup(f => f.FileName).Returns("image.png");
        var service = BuildService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadDocumentAsync(nonPdf.Object, CancellationToken.None));
    }

    [Fact]
    public async Task UploadDocumentAsync_WhenValidPdf_SavesDocumentAndPublishesToRabbitMQ()
    {
        // Arrange
        SetupRabbitPublishSuccess();
        var file = BuildPdfFileMock("document.pdf");
        var service = BuildService();

        // Act
        var documentId = await service.UploadDocumentAsync(file.Object, CancellationToken.None);

        // Assert
        var saved = await _context.Documents.FindAsync(documentId);
        Assert.NotNull(saved);
        Assert.Equal("document.pdf", saved.OriginalFileName);
        Assert.Equal(DocumentStatus.Uploaded, saved.Status);
        _rabbitMock.Verify(c => c.CreateChannelAsync(
            It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadDocumentAsync_WhenRabbitMQFails_SetsStatusToFailedAndRethrows()
    {
        // Arrange
        _rabbitMock
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("RabbitMQ unavailable"));
        var file = BuildPdfFileMock("fail.pdf");
        var service = BuildService();

        // Act
        await Assert.ThrowsAsync<Exception>(() =>
            service.UploadDocumentAsync(file.Object, CancellationToken.None));

        // Assert
        var saved = await _context.Documents.FirstOrDefaultAsync(d => d.OriginalFileName == "fail.pdf");
        Assert.NotNull(saved);
        Assert.Equal(DocumentStatus.Failed, saved.Status);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_tempUploadsPath))
            Directory.Delete(_tempUploadsPath, recursive: true);
    }
}
