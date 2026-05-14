using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ApiGateway.Data;
using ApiGateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using RabbitMQ.Client;
using Shared.Contracts;
using Shared.Models;

namespace ApiGateway.Tests.IntegrationTests;

// Replaces Postgres + RabbitMQ with in-memory/mock and injects a per-test IDocumentService mock.
internal sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public Mock<IDocumentService> DocumentServiceMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            ReplaceDbContext(services);
            Replace<IConnection>(services, _ => new Mock<IConnection>().Object);
            Replace<IDocumentService>(services, _ => DocumentServiceMock.Object, ServiceLifetime.Scoped);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DocumentContext>().Database.EnsureCreated();
        return host;
    }

    private static void ReplaceDbContext(IServiceCollection services)
    {
        var toRemove = services
            .Where(d =>
                d.ServiceType == typeof(DbContextOptions<DocumentContext>) ||
                d.ServiceType == typeof(DocumentContext) ||
                (d.ServiceType.IsGenericType &&
                 d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration") &&
                 d.ServiceType.GenericTypeArguments is [{ } t] && t == typeof(DocumentContext)))
            .ToList();

        foreach (var d in toRemove)
            services.Remove(d);

        services.AddDbContext<DocumentContext>(opt => opt.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
    }

    private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton) where T : class
    {
        var existing = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (existing is not null) services.Remove(existing);
        services.Add(new ServiceDescriptor(typeof(T), factory, lifetime));
    }
}

public class DocumentsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly TestWebApplicationFactory _factory = new();
    private Mock<IDocumentService> ServiceMock => _factory.DocumentServiceMock;

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private HttpClient CreateClient() => _factory.CreateClient();

    private static MultipartFormDataContent BuildPdfMultipart(string fileName = "test.pdf", string contentType = "application/pdf")
    {
        var content = new MultipartFormDataContent();
        var fileBytes = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        fileBytes.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileBytes, "file", fileName);
        return content;
    }

    // -------------------------------------------------------------------------
    // POST /documents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDocuments_WhenNoFileSent_Returns400()
    {
        // Arrange
        var client = CreateClient();
        using var content = new MultipartFormDataContent();

        // Act
        var response = await client.PostAsync("/documents", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDocuments_WhenFileIsNotPdf_Returns400()
    {
        // Arrange
        var client = CreateClient();
        using var content = BuildPdfMultipart("image.png", contentType: "image/png");

        // Act
        var response = await client.PostAsync("/documents", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDocuments_WhenValidPdfUploaded_Returns201WithDocumentId()
    {
        // Arrange
        var expectedId = Guid.NewGuid();
        ServiceMock
            .Setup(s => s.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);
        var client = CreateClient();
        using var content = BuildPdfMultipart();

        // Act
        var response = await client.PostAsync("/documents", content);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var returnedId = JsonSerializer.Deserialize<Guid>(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedId, returnedId);
    }

    [Fact]
    public async Task PostDocuments_WhenServiceThrowsUnexpectedException_Returns500()
    {
        // Arrange
        ServiceMock
            .Setup(s => s.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));
        var client = CreateClient();
        using var content = BuildPdfMultipart();

        // Act
        var response = await client.PostAsync("/documents", content);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // GET /documents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocuments_WhenNoDocumentsExist_Returns200WithEmptyArray()
    {
        // Arrange
        ServiceMock
            .Setup(s => s.GetDocumentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var client = CreateClient();

        // Act
        var response = await client.GetAsync("/documents");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<DocumentListResponse[]>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task GetDocuments_WhenDocumentsExist_Returns200WithList()
    {
        // Arrange
        var docs = new[]
        {
            new DocumentListResponse { Id = Guid.NewGuid(), OriginalFileName = "a.pdf", Status = DocumentStatus.Uploaded, UploadedAt = DateTimeOffset.UtcNow },
            new DocumentListResponse { Id = Guid.NewGuid(), OriginalFileName = "b.pdf", Status = DocumentStatus.Completed, UploadedAt = DateTimeOffset.UtcNow }
        };
        ServiceMock
            .Setup(s => s.GetDocumentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);
        var client = CreateClient();

        // Act
        var response = await client.GetAsync("/documents");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<DocumentListResponse[]>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.Equal(2, result!.Length);
    }

    // -------------------------------------------------------------------------
    // GET /documents/{id}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocumentById_WhenDocumentDoesNotExist_Returns404()
    {
        // Arrange
        ServiceMock
            .Setup(s => s.GetDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentResponse?)null);
        var client = CreateClient();

        // Act
        var response = await client.GetAsync($"/documents/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentById_WhenDocumentExists_Returns200WithDetails()
    {
        // Arrange
        var id = Guid.NewGuid();
        var doc = new DocumentResponse
        {
            Id = id,
            OriginalFileName = "report.pdf",
            Status = DocumentStatus.Completed,
            UploadedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ExtractedText = "Extracted content here"
        };
        ServiceMock
            .Setup(s => s.GetDocumentAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(doc);
        var client = CreateClient();

        // Act
        var response = await client.GetAsync($"/documents/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<DocumentResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions);
        Assert.Equal(id, result!.Id);
        Assert.Equal("report.pdf", result.OriginalFileName);
        Assert.Equal(DocumentStatus.Completed, result.Status);
        Assert.Equal("Extracted content here", result.ExtractedText);
    }

    public void Dispose() => _factory.Dispose();
}
