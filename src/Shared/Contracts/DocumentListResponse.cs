using System.ComponentModel.DataAnnotations;
using Shared.Models;

namespace Shared.Contracts;

public class DocumentListResponse
{
    public Guid Id { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public DocumentStatus Status { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}