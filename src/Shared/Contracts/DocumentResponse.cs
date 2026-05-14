using System.ComponentModel.DataAnnotations;
using Shared.Models;

namespace Shared.Contracts;

public class DocumentResponse
{
    public Guid Id { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public DocumentStatus Status { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? ExtractedText { get; set; }
}