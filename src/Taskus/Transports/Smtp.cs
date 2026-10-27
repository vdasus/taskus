using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Transports;

public sealed record SmtpOptions(string Server, int Port, string? User, string? Password, string From)
{
    public static Result<SmtpOptions> Create(Settings settings)
    {
        var server = settings.Required("server");
        var port = settings.Int("port", 587);
        var from = settings.Required("from");
        return Result.Combine("; ", server, port, from)
            .Map(() => new SmtpOptions(server.Value, port.Value, settings.Optional("user"), settings.Optional("password"), from.Value));
    }

    public async Task<SmtpClient> ConnectAsync(CancellationToken ct)
    {
        var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(Server, Port, SecureSocketOptions.Auto, ct);
            if (User is not null)
                await client.AuthenticateAsync(User, Password ?? "", ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public MimeMessage Message(string to, string subject, string? cc = null)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(MailboxAddress.Parse(From));
        message.To.AddRange(InternetAddressList.Parse(to.Replace(';', ',')));
        if (!string.IsNullOrWhiteSpace(cc))
            message.Cc.AddRange(InternetAddressList.Parse(cc.Replace(';', ',')));
        return message;
    }
}

/// <summary>Sends one e-mail per row. Fields: <c>to</c>, <c>subject</c>, <c>body</c>, optional <c>cc</c>.</summary>
public sealed class SmtpSink(SmtpOptions options, bool html) : ISink
{
    public static Result<ISink> Create(Settings settings, Settings? smtp)
    {
        var html = settings.Bool("html", false);
        var options = smtp is null ? Result.Failure<SmtpOptions>("an smtp sink needs an smtp section") : SmtpOptions.Create(smtp);
        return Result.Combine("; ", html, options).Map(() => (ISink)new SmtpSink(options.Value, html.Value));
    }

    public async Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        try
        {
            return new Session(await options.ConnectAsync(ct), options, html);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or MailKit.ProtocolException or AuthenticationException)
        {
            return Result.Failure<ISinkSession>($"smtp {options.Server}:{options.Port}: {ex.Message}");
        }
    }

    private sealed class Session(SmtpClient client, SmtpOptions options, bool html) : ISinkSession
    {
        public bool AllOrNothing => false; // a sent e-mail cannot be recalled

        public async Task<Result> WriteAsync(Row row, CancellationToken ct)
        {
            if (Text(row, "to") is not { Length: > 0 } to)
                return Result.Failure("field 'to' is empty");

            try
            {
                var message = options.Message(to, Text(row, "subject") ?? "", Text(row, "cc"));
                message.Body = new TextPart(html ? "html" : "plain") { Text = Text(row, "body") ?? "" };
                await client.SendAsync(message, ct);
                return Result.Success();
            }
            catch (Exception ex) when (ex is ParseException or SmtpCommandException or SmtpProtocolException)
            {
                return Result.Failure(ex.Message);
            }
        }

        public Task<Result> CompleteAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public async ValueTask DisposeAsync()
        {
            if (client.IsConnected)
                await client.DisconnectAsync(quit: true);
            client.Dispose();
        }

        private static string? Text(Row row, string field) => row.GetValueOrDefault(field)?.ToString();
    }
}
