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

/// <summary>
/// Custom factory that replaces real infrastructure (Postgres, RabbitMQ) with in-memory/mock
/// versions and exposes a slot to inject a per-test IDocumentService mock.
/// </summary>
internal sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public Mock<IDocumentService> DocumentServiceMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ---- DbContext: swap Npgsql → InMemory -------------------------
            // Remove every descriptor that carries the Npgsql configuration so
            // that EF doesn't end up with two registered database providers.
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<DocumentContext>) ||
                    d.ServiceType == typeof(DocumentContext) ||
                    // IDbContextOptionsConfiguration<DocumentContext> – holds the Npgsql extension
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition().Name.StartsWith("IDbContextOptionsConfiguration") &&
                     d.ServiceType.GenericTypeArguments is [{ } t] && t == typeof(DocumentContext)))
                .ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            services.AddDbContext<DocumentContext>(opt =>
                opt.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

            // ---- RabbitMQ: replace with a no-op mock -----------------------
            var rabbitDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IConnection));
            if (rabbitDesc is not null)
                services.Remove(rabbitDesc);

            services.AddSingleton<IConnection>(_ => new Mock<IConnection>().Object);

            // ---- IDocumentService: inject the per-test mock ----------------
            var svcDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IDocumentService));
            if (svcDesc is not null)
                services.Remove(svcDesc);

            services.AddScoped<IDocumentService>(_ => DocumentServiceMock.Object);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Build the host normally; then replace MigrateAsync with EnsureCreated
        // so InMemory databases don't fail on the relational migration call.
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentContext>();
        db.Database.EnsureCreated();

        return host;
    }
}

public class DocumentsApiTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;

    public DocumentsApiTests()
    {
        _factory = new TestWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    private Mock<IDocumentService> Mock => _factory.DocumentServiceMock;
    private HttpClient CreateClient() => _factory.CreateClient();

    // -------------------------------------------------------------------------
    // POST /documents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDocuments_Returns400_WhenNoFileSent()
    {
        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var response = await client.PostAsync("/documents", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDocuments_Returns400_WhenFileIsNotPdf()
    {
        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[] { 1, 2, 3 });
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(fileContent, "file", "image.png");

        var response = await client.PostAsync("/documents", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostDocuments_Returns201_WhenValidPdfUploaded()
    {
        var expectedId = Guid.NewGuid();
        Mock
            .Setup(s => s.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedId);

        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "test.pdf");

        var response = await client.PostAsync("/documents", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var returnedId = JsonSerializer.Deserialize<Guid>(body);
        Assert.Equal(expectedId, returnedId);
    }

    [Fact]
    public async Task PostDocuments_Returns500_WhenServiceThrowsUnexpectedException()
    {
        Mock
            .Setup(s => s.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected error"));

        var client = CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "test.pdf");

        var response = await client.PostAsync("/documents", content);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // GET /documents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocuments_Returns200_WithEmptyArray_WhenNoDocuments()
    {
        Mock
            .Setup(s => s.GetDocumentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentListResponse>());

        var client = CreateClient();
        var response = await client.GetAsync("/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<DocumentListResponse[]>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDocuments_Returns200_WithListOfDocuments()
    {
        var docs = new[]
        {
            new DocumentListResponse { Id = Guid.NewGuid(), OriginalFileName = "a.pdf", Status = DocumentStatus.Uploaded, UploadedAt = DateTimeOffset.UtcNow },
            new DocumentListResponse { Id = Guid.NewGuid(), OriginalFileName = "b.pdf", Status = DocumentStatus.Completed, UploadedAt = DateTimeOffset.UtcNow }
        };

        Mock
            .Setup(s => s.GetDocumentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);

        var client = CreateClient();
        var response = await client.GetAsync("/documents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<DocumentListResponse[]>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
    }

    // -------------------------------------------------------------------------
    // GET /documents/{id}
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDocumentById_Returns404_WhenNotFound()
    {
        Mock
            .Setup(s => s.GetDocumentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentResponse?)null);

        var client = CreateClient();
        var response = await client.GetAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetDocumentById_Returns200_WithDocumentDetails_WhenFound()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var doc = new DocumentResponse
        {
            Id = id,
            OriginalFileName = "test.pdf",
            Status = DocumentStatus.Completed,
            UploadedAt = now,
            ProcessedAt = now.AddMinutes(1),
            ExtractedText = "Hello PDF"
        };

        Mock
            .Setup(s => s.GetDocumentAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(doc);

        var client = CreateClient();
        var response = await client.GetAsync($"/documents/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<DocumentResponse>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("test.pdf", result.OriginalFileName);
        Assert.Equal(DocumentStatus.Completed, result.Status);
        Assert.Equal("Hello PDF", result.ExtractedText);
    }
}
