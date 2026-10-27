using FluentValidation;
using FluentValidation.Results;
using MimeKit;
using Taskus.Mapping;

namespace Taskus.Config;

/// <summary>Rules a bound task must satisfy before anything runs. Every failed rule is reported at once.</summary>
public static class TaskValidation
{
    private static readonly TaskValidator Validator = new();

    public static Result<TaskOptions> Validate(TaskOptions task)
    {
        var result = Validator.Validate(task);
        return result.IsValid ? task : Result.Failure<TaskOptions>(string.Join("; ", result.Errors.Select(Describe)));
    }

    // FluentValidation paths are .NET member names ("Sink.Url"); task files use camelCase ("sink.url").
    private static string Describe(ValidationFailure failure) =>
        failure.PropertyName.Length == 0
            ? failure.ErrorMessage
            : $"{string.Join('.', failure.PropertyName.Split('.').Select(s => char.ToLowerInvariant(s[0]) + s[1..]))}: {failure.ErrorMessage}";

    internal static void Template<T>(string? text, ValidationContext<T> context)
    {
        if (text is not null && TaskTemplate.Parse(text) is { IsFailure: true } parsed)
            context.AddFailure(parsed.Error);
    }

    // MimeKit accepts a bare local part ("admin"); a mail server needs the domain too.
    internal static bool AreAddresses(string? text) =>
        text is not null && InternetAddressList.TryParse(text.Replace(';', ','), out var list) && list.Count > 0
        && list.Mailboxes.All(m => m.Address.Contains('@'));

    private sealed class TaskValidator : AbstractValidator<TaskOptions>
    {
        public TaskValidator()
        {
            RuleFor(x => x.Source).SetInheritanceValidator(v => v
                .Add(new ExcelSourceValidator())
                .Add(new PostgresSourceValidator())
                .Add(new HttpSourceValidator()));
            RuleFor(x => x.Sink).SetInheritanceValidator(v => v
                .Add(new ExcelSinkValidator())
                .Add(new PostgresSinkValidator())
                .Add(new HttpSinkValidator()));
            RuleFor(x => x.Map).Custom((map, context) =>
            {
                foreach (var (field, text) in map)
                {
                    if (field.Length == 0)
                        context.AddFailure("field names must not be empty");
                    else if (TaskTemplate.Parse(text) is { IsFailure: true } parsed)
                        context.AddFailure($"field '{field}': {parsed.Error}");
                }
            });
            RuleFor(x => x.Name).Must(name => name!.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).When(x => x.Name is not null)
                .WithMessage("must not contain characters that a file name cannot have; it names the report file");
            RuleFor(x => x.MaxErrors).GreaterThanOrEqualTo(0).WithMessage("must not be negative");
            RuleForEach(x => x.LogExclude).NotEmpty().WithMessage("field names must not be empty");
            RuleFor(x => x.Smtp).NotNull().When(x => x.Report.MailTo is not null).WithMessage("is required by report.mailTo");
            RuleFor(x => x.Smtp).NotNull().When(x => x.Sink is SmtpSinkOptions).WithMessage("is required by the smtp sink");
            RuleFor(x => x.Smtp!).SetValidator(new SmtpValidator()).When(x => x.Smtp is not null);
            RuleFor(x => x.Report.MailTo).Must(AreAddresses).When(x => x.Report.MailTo is not null)
                .WithMessage("must be e-mail addresses separated by , or ;");
        }
    }

    private sealed class SmtpValidator : AbstractValidator<SmtpOptions>
    {
        public SmtpValidator()
        {
            RuleFor(x => x.Server).NotEmpty().WithMessage("must not be empty");
            RuleFor(x => x.Port).InclusiveBetween(1, 65535).WithMessage("must be 1 to 65535");
            RuleFor(x => x.From).Must(from => MailboxAddress.TryParse(from, out var mailbox) && mailbox.Address.Contains('@')).WithMessage("must be an e-mail address");
        }
    }

    private sealed class ExcelSourceValidator : AbstractValidator<ExcelSourceOptions>
    {
        public ExcelSourceValidator()
        {
            RuleFor(x => x.Folder).NotEmpty().WithMessage("must not be empty");
            RuleFor(x => x.Pattern).NotEmpty().WithMessage("must not be empty");
        }
    }

    private sealed class PostgresSourceValidator : AbstractValidator<PostgresSourceOptions>
    {
        public PostgresSourceValidator()
        {
            RuleFor(x => x.Connection).NotEmpty().WithMessage("must not be empty");
            RuleFor(x => x.Query).NotEmpty().WithMessage("must not be empty");
        }
    }

    private sealed class ExcelSinkValidator : AbstractValidator<ExcelSinkOptions>
    {
        public ExcelSinkValidator()
        {
            RuleFor(x => x.Folder).NotEmpty().WithMessage("must not be empty");
            RuleFor(x => x.File).NotEmpty().WithMessage("must not be empty").Custom(Template);
            RuleFor(x => x.Sheet).NotEmpty().WithMessage("must not be empty");
        }
    }

    private sealed class PostgresSinkValidator : AbstractValidator<PostgresSinkOptions>
    {
        public PostgresSinkValidator()
        {
            RuleFor(x => x.Connection).NotEmpty().WithMessage("must not be empty");
            RuleFor(x => x.Table).NotEmpty().WithMessage("must not be empty");
        }
    }

    private abstract class HttpEndpointValidator<T> : AbstractValidator<T> where T : IHttpEndpointOptions
    {
        protected HttpEndpointValidator()
        {
            RuleFor(x => x.Method).IsInEnum().WithMessage("must be GET | POST | PUT | PATCH | DELETE");
            RuleFor(x => x.Url).NotEmpty().WithMessage("must not be empty").Custom(Template);
            RuleFor(x => x.Timeout).GreaterThan(0).WithMessage("must be positive");
            RuleFor(x => x.RateLimit).GreaterThanOrEqualTo(0).WithMessage("must not be negative");
            RuleFor(x => x.Headers.Keys).Must(keys => keys.All(k => k.Length > 0)).WithName("headers").WithMessage("names must not be empty");
            RuleFor(x => x.Retry!.Attempts).GreaterThanOrEqualTo(0).When(x => x.Retry is not null).WithMessage("must not be negative");
            RuleFor(x => x.Retry!.Delay).GreaterThanOrEqualTo(0).When(x => x.Retry is not null).WithMessage("must not be negative");
        }
    }

    private sealed class HttpSourceValidator : HttpEndpointValidator<HttpSourceOptions>
    {
        public HttpSourceValidator()
        {
            RuleFor(x => x.Body).Custom(Template);
            RuleFor(x => x.Body)
                .Must((options, _) => options.Method is HttpMethodName.Post or HttpMethodName.Put or HttpMethodName.Patch)
                .When(x => x.Body is not null).WithMessage("needs method POST, PUT or PATCH");
        }
    }

    private sealed class HttpSinkValidator : HttpEndpointValidator<HttpSinkOptions>;
}
