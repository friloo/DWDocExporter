using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DwDocExport;

/// <summary>
/// Versendet Benachrichtigungen über den Abschluss bzw. Fehler eines Laufs –
/// per SMTP-E-Mail und/oder Webhook (Teams/Slack-kompatibles {"text":...}).
/// Fehler beim Versand werden geschluckt (dürfen den Export nicht stören).
/// </summary>
public static class Notifier
{
    public static async Task SendAsync(ExporterOptions opt, string subject, string body, CancellationToken ct)
    {
        await TrySendMailAsync(opt, subject, body, ct).ConfigureAwait(false);
        await TrySendWebhookAsync(opt, subject, body, ct).ConfigureAwait(false);
    }

    private static async Task TrySendMailAsync(ExporterOptions opt, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.SmtpHost) ||
            string.IsNullOrWhiteSpace(opt.SmtpFrom) ||
            string.IsNullOrWhiteSpace(opt.SmtpTo))
            return;

        try
        {
            using var msg = new MailMessage(opt.SmtpFrom, opt.SmtpTo)
            {
                Subject = subject,
                Body = body
            };
            using var client = new SmtpClient(opt.SmtpHost, opt.SmtpPort)
            {
                EnableSsl = opt.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };
            if (!string.IsNullOrWhiteSpace(opt.SmtpUser))
                client.Credentials = new NetworkCredential(opt.SmtpUser, opt.SmtpPassword);

            await client.SendMailAsync(msg, ct).ConfigureAwait(false);
        }
        catch
        {
            // Versand-Fehler ignorieren.
        }
    }

    private static async Task TrySendWebhookAsync(ExporterOptions opt, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.WebhookUrl))
            return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var payload = JsonSerializer.Serialize(new { text = $"**{subject}**\n{body}" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(opt.WebhookUrl, content, ct).ConfigureAwait(false);
        }
        catch
        {
            // Versand-Fehler ignorieren.
        }
    }
}
