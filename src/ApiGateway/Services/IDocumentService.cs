using Shared.Contracts;
using Shared.Models;

namespace ApiGateway.Services;

public interface IDocumentService
{
    Task<Guid> UploadDocumentAsync(IFormFile file, CancellationToken cancellationToken);
    Task<DocumentListResponse[]> GetDocumentsAsync(CancellationToken cancellationToken);
    Task<DocumentResponse?> GetDocumentAsync(Guid id, CancellationToken cancellationToken);
}