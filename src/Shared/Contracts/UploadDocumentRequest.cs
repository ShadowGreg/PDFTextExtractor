using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace Shared.Contracts;

public class UploadDocumentRequest
{
    [Required]
    public IFormFile File { get; set; } = null!;
}