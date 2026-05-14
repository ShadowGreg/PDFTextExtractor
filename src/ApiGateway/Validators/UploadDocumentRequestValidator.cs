using FluentValidation;
using Shared.Contracts;

namespace ApiGateway.Validators;

public class UploadDocumentRequestValidator : AbstractValidator<UploadDocumentRequest>
{
    public UploadDocumentRequestValidator()
    {
        RuleFor(x => x.File)
            .NotNull().WithMessage("File is required")
            .Must(x => x is not null && x.Length > 0).WithMessage("File must not be empty")
            .Must(x => x is not null && x.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                .WithMessage("File must be a PDF");
    }
}