using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared.Models;
using BackgroundWorker.Data;

namespace BackgroundWorker.Services;

public class DocumentProcessor : BackgroundService
{
    private readonly IConnection _rabbitConnection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(IConnection rabbitConnection, IServiceScopeFactory scopeFactory, ILogger<DocumentProcessor> logger)
    {
        _rabbitConnection = rabbitConnection;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentProcessor is starting");

        await using var channel = await _rabbitConnection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            queue: "document_processing_queue",
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: stoppingToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var documentId = Guid.Parse(Encoding.UTF8.GetString(ea.Body.ToArray()));
            _logger.LogInformation("Processing document {DocumentId}", documentId);

            try
            {
                await ProcessDocumentAsync(documentId, stoppingToken);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                _logger.LogInformation("Completed processing document {DocumentId}", documentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing document {DocumentId}", documentId);
                // requeue only on first failure; discard if already redelivered to avoid infinite loop
                var requeue = !ea.Redelivered;
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: requeue, cancellationToken: stoppingToken);
                if (!requeue)
                    _logger.LogWarning("Document {DocumentId} exceeded retry limit, discarding message", documentId);
            }
        };

        await channel.BasicConsumeAsync(
            queue: "document_processing_queue",
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation("DocumentProcessor is running");

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ProcessDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DocumentContext>();

        var document = await context.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document == null)
        {
            _logger.LogWarning("Document {DocumentId} not found in database", documentId);
            return;
        }

        try
        {
            document.Status = DocumentStatus.Processing;
            await context.SaveChangesAsync(cancellationToken);

            var extractedText = ExtractTextFromPdf(document.FilePath);

            document.ExtractedText = extractedText;
            document.Status = DocumentStatus.Completed;
            document.ProcessedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Successfully extracted text from document {DocumentId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            document.Status = DocumentStatus.Failed;
            await context.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private string ExtractTextFromPdf(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found at path: {filePath}");

        var readerProperties = new ReaderProperties().SetPassword(Array.Empty<byte>());
        using var pdfReader = new PdfReader(filePath, readerProperties);
        pdfReader.SetUnethicalReading(true);
        using var pdfDoc = new PdfDocument(pdfReader);

        var text = new StringBuilder();
        for (var i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
        {
            var pageText = PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i), new LocationTextExtractionStrategy());
            text.AppendLine(pageText);
        }

        return text.ToString();
    }
}
