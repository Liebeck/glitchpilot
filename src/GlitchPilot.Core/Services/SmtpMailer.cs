using GlitchPilot.Core.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace GlitchPilot.Core.Services;

public sealed class SmtpMailer : ISmtpMailer
{
    private readonly ILogger<SmtpMailer> _log;
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _from;
    private readonly List<string> _recipients;

    public SmtpMailer(ILogger<SmtpMailer> log)
    {
        _log = log;
        _host = EnvironmentConfig.SmtpHost;
        _port = EnvironmentConfig.SmtpPort;
        _username = EnvironmentConfig.SmtpUsername;
        _password = EnvironmentConfig.SmtpPassword;
        _from = EnvironmentConfig.MailFrom;
        _recipients = EnvironmentConfig.MailRecipients;
    }

    public async Task DeliverAsync(string subject, string htmlBody, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_from));
        foreach (var addr in _recipients)
            msg.To.Add(MailboxAddress.Parse(addr.Trim()));
        msg.Subject = subject;
        msg.Body = new TextPart("html") { Text = htmlBody };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_host, _port, SecureSocketOptions.StartTls, ct);
        await smtp.AuthenticateAsync(_username, _password, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(quit: true, ct);

        _log.LogInformation("Delivered: {Subject}", subject);
    }
}
