using Microsoft.AspNetCore.Identity.UI.Services;

namespace IndiAsset.Services
{
    public class EmailSender : IEmailSender
    {
        private readonly ILogger<EmailSender> _logger;

        public EmailSender(ILogger<EmailSender> logger)
        {
            _logger = logger;
        }

        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            _logger.LogInformation("Email dispatched to {Email} with subject: {Subject}", email, subject);
            return Task.CompletedTask;
        }
    }
}
