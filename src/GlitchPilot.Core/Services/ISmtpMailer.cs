namespace GlitchPilot.Core.Services;

public interface ISmtpMailer
{
    Task DeliverAsync(string subject, string htmlBody, CancellationToken ct = default);
}
