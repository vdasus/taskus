using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Serilog;
using Taskus.Config;
using Taskus.Pipeline;

namespace Taskus.Transports;

/// <summary>Connects to the server and builds messages from <see cref="SmtpOptions"/>.</summary>
public static class Mail
{
    public static async Task<SmtpClient> ConnectAsync(this SmtpOptions options, ILogger log, CancellationToken ct)
    {
        var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(options.Server, options.Port, SecureSocketOptions.Auto, ct);
            if (options.User is not null)
                await client.AuthenticateAsync(options.User, options.Password ?? "", ct);
            log.Debug("Connected to {Server}:{Port}, authenticated as {User}", options.Server, options.Port, options.User ?? "(anonymous)");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static MimeMessage Message(this SmtpOptions options, string to, string subject, string? cc = null)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.AddRange(InternetAddressList.Parse(to.Replace(';', ',')));
        if (!string.IsNullOrWhiteSpace(cc))
            message.Cc.AddRange(InternetAddressList.Parse(cc.Replace(';', ',')));
        return message;
    }
}

/// <summary>Sends one e-mail per row. Fields: <c>to</c>, <c>subject</c>, <c>body</c>, optional <c>cc</c>.</summary>
public sealed class SmtpSink(SmtpOptions options, bool html, ILogger log) : ISink
{
    public async Task<Result<ISinkSession>> OpenAsync(string unit, CancellationToken ct)
    {
        try
        {
            return new Session(await options.ConnectAsync(log, ct), options, html, log);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or MailKit.ProtocolException or AuthenticationException)
        {
            return Result.Failure<ISinkSession>($"smtp {options.Server}:{options.Port}: {ex.Message}");
        }
    }

    private sealed class Session(SmtpClient client, SmtpOptions options, bool html, ILogger log) : ISinkSession
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
                log.Verbose("Mail sent to {RecipientCount} recipients", message.To.Count + message.Cc.Count); // addresses are row data
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
