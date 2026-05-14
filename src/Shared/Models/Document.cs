using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

public enum DocumentStatus
{
    Uploaded,
    Processing,
    Completed,
    Failed
}

public class Document
{
    public Guid Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    public string? ExtractedText { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }
}