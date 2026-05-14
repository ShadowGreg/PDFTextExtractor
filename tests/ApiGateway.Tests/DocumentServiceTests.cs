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
    private readonly Mock<ILogger<DocumentService>> _loggerMock;
    private readonly IConfiguration _configuration;
    private readonly string _tempUploadsPath;

    public DocumentServiceTests()
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DocumentContext(options);

        _channelMock = new Mock<IChannel>();
        _rabbitMock = new Mock<IConnection>();
        _rabbitMock.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(_channelMock.Object);

        _loggerMock = new Mock<ILogger<DocumentService>>();
        _tempUploadsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Uploads:Path"] = _tempUploadsPath
            })
            .Build();
        _configuration = config;
    }

    [Fact]
    public async Task GetDocumentsAsync_ReturnsAllDocuments()
    {
        _context.Documents.AddRange(
            new Document { Id = Guid.NewGuid(), OriginalFileName = "a.pdf", FilePath = "/a.pdf" },
            new Document { Id = Guid.NewGuid(), OriginalFileName = "b.pdf", FilePath = "/b.pdf" }
        );
        await _context.SaveChangesAsync();

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);
        var result = await service.GetDocumentsAsync(CancellationToken.None);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsNull_WhenNotFound()
    {
        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);
        var result = await service.GetDocumentAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDocumentAsync_ReturnsDocument_WhenFound()
    {
        var id = Guid.NewGuid();
        _context.Documents.Add(new Document { Id = id, OriginalFileName = "test.pdf", FilePath = "/test.pdf" });
        await _context.SaveChangesAsync();

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);
        var result = await service.GetDocumentAsync(id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("test.pdf", result.OriginalFileName);
    }

    [Fact]
    public async Task UploadDocumentAsync_ThrowsArgumentException_WhenNotPdf()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        fileMock.Setup(f => f.ContentType).Returns("image/png");
        fileMock.Setup(f => f.FileName).Returns("image.png");

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadDocumentAsync(fileMock.Object, CancellationToken.None));
    }

    [Fact]
    public async Task UploadDocumentAsync_ThrowsArgumentException_WhenEmptyFile()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(0);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.FileName).Returns("empty.pdf");

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UploadDocumentAsync(fileMock.Object, CancellationToken.None));
    }

    [Fact]
    public async Task UploadDocumentAsync_SavesDocumentWithCorrectFields_AndPublishesToRabbitMQ()
    {
        var fileMock = new Mock<IFormFile>();
        var fileName = "document.pdf";
        var fileContent = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var stream = new MemoryStream(fileContent);

        fileMock.Setup(f => f.Length).Returns(fileContent.Length);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Callback<Stream, CancellationToken>((s, ct) => stream.CopyTo(s))
                .Returns(Task.CompletedTask);

        _channelMock.Setup(c => c.QueueDeclareAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RabbitMQ.Client.QueueDeclareOk("document_processing_queue", 0, 0));

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);
        var documentId = await service.UploadDocumentAsync(fileMock.Object, CancellationToken.None);

        var saved = await _context.Documents.FindAsync(documentId);
        Assert.NotNull(saved);
        Assert.Equal(fileName, saved.OriginalFileName);
        Assert.Equal(DocumentStatus.Uploaded, saved.Status);
        Assert.NotEqual(Guid.Empty, saved.Id);

        // Verify the RabbitMQ channel was created (which means publish was attempted)
        _rabbitMock.Verify(c => c.CreateChannelAsync(
            It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadDocumentAsync_SetsStatusToFailed_WhenRabbitMQPublishFails()
    {
        var fileMock = new Mock<IFormFile>();
        var fileName = "fail.pdf";
        var fileContent = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var stream = new MemoryStream(fileContent);

        fileMock.Setup(f => f.Length).Returns(fileContent.Length);
        fileMock.Setup(f => f.ContentType).Returns("application/pdf");
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Callback<Stream, CancellationToken>((s, ct) => stream.CopyTo(s))
                .Returns(Task.CompletedTask);

        // Make RabbitMQ channel creation throw
        _rabbitMock.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new Exception("RabbitMQ unavailable"));

        var service = new DocumentService(_context, _rabbitMock.Object, _loggerMock.Object, _configuration);

        await Assert.ThrowsAsync<Exception>(() =>
            service.UploadDocumentAsync(fileMock.Object, CancellationToken.None));

        var failed = await _context.Documents
            .FirstOrDefaultAsync(d => d.OriginalFileName == fileName);
        Assert.NotNull(failed);
        Assert.Equal(DocumentStatus.Failed, failed.Status);
    }

    public void Dispose()
    {
        _context.Dispose();
        if (Directory.Exists(_tempUploadsPath))
            Directory.Delete(_tempUploadsPath, recursive: true);
    }
}
