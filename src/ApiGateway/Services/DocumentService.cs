using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Shared.Contracts;
using Shared.Models;
using ApiGateway.Data;

namespace ApiGateway.Services;

public class DocumentService : IDocumentService
{
    private readonly DocumentContext _context;
    private readonly IConnection _rabbitConnection;
    private readonly ILogger<DocumentService> _logger;
    private readonly string _uploadsPath;

    public DocumentService(DocumentContext context, IConnection rabbitConnection, ILogger<DocumentService> logger, IConfiguration configuration)
    {
        _context = context;
        _rabbitConnection = rabbitConnection;
        _logger = logger;
        _uploadsPath = configuration["Uploads:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        Directory.CreateDirectory(_uploadsPath);
    }

    public async Task<Guid> UploadDocumentAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            throw new ArgumentException("File is empty");

        if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("File must be a PDF");

        var documentId = Guid.NewGuid();
        var fileName = Path.GetFileName(file.FileName);
        var filePath = Path.Combine(_uploadsPath, documentId.ToString(), fileName);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var document = new Document
        {
            Id = documentId,
            OriginalFileName = fileName,
            FilePath = filePath,
            Status = DocumentStatus.Uploaded
        };

        _context.Documents.Add(document);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await SendProcessMessageAsync(documentId, cancellationToken);
            _logger.LogInformation("Sent process message for document {DocumentId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to RabbitMQ for document {DocumentId}", documentId);
            document.Status = DocumentStatus.Failed;
            await _context.SaveChangesAsync(cancellationToken);
            throw;
        }

        return documentId;
    }

    public async Task<DocumentListResponse[]> GetDocumentsAsync(CancellationToken cancellationToken)
    {
        return await _context.Documents
            .Select(d => new DocumentListResponse
            {
                Id = d.Id,
                OriginalFileName = d.OriginalFileName,
                Status = d.Status,
                UploadedAt = d.UploadedAt
            })
            .ToArrayAsync(cancellationToken);
    }

    public async Task<DocumentResponse?> GetDocumentAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _context.Documents
            .Where(d => d.Id == id)
            .Select(d => new DocumentResponse
            {
                Id = d.Id,
                OriginalFileName = d.OriginalFileName,
                Status = d.Status,
                UploadedAt = d.UploadedAt,
                ProcessedAt = d.ProcessedAt,
                ExtractedText = d.ExtractedText
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task SendProcessMessageAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var channel = await _rabbitConnection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: "document_processing_queue",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(documentId.ToString());
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: "document_processing_queue",
            mandatory: false,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogInformation("Sent message to queue for document {DocumentId}", documentId);
    }
}
