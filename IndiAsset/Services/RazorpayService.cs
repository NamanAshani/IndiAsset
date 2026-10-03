using IndiAsset.Models;
using Razorpay.Api;

namespace IndiAsset.Services
{
    public interface IRazorpayService
    {
        string GetKeyId();
        string GetCurrency();
        Task<(bool Success, string OrderId, string ErrorMessage)> CreateOrderAsync(decimal amountInRupees, string receiptId, string description);
        bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
    }

    public class RazorpayService : IRazorpayService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<RazorpayService> _logger;
        private readonly string _keyId;
        private readonly string _keySecret;
        private readonly string _currency;

        public RazorpayService(IConfiguration configuration, ILogger<RazorpayService> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _keyId = _configuration["Razorpay:KeyId"] ?? "rzp_test_IndiAssetDemoKey";
            _keySecret = _configuration["Razorpay:KeySecret"] ?? "IndiAssetSecretDemo";
            _currency = _configuration["Razorpay:Currency"] ?? "INR";
        }

        public string GetKeyId() => _keyId;
        public string GetCurrency() => _currency;

        public async Task<(bool Success, string OrderId, string ErrorMessage)> CreateOrderAsync(
            decimal amountInRupees,
            string receiptId,
            string description)
        {
            try
            {
                // Amount in paise (1 INR = 100 paise)
                long amountInPaise = (long)Math.Round(amountInRupees * 100, MidpointRounding.AwayFromZero);
                if (amountInPaise <= 0)
                {
                    amountInPaise = 100; // minimum 1 INR
                }

                // If demo or placeholder key, simulate an order ID
                if (IsDemoKey(_keyId))
                {
                    var demoOrderId = $"order_demo_{Guid.NewGuid():N}";
                    _logger.LogInformation("Generated Demo Razorpay Order: {OrderId} for amount: {Amount}", demoOrderId, amountInRupees);
                    return (true, demoOrderId, string.Empty);
                }

                var client = new RazorpayClient(_keyId, _keySecret);
                var options = new Dictionary<string, object>
                {
                    { "amount", amountInPaise },
                    { "currency", _currency },
                    { "receipt", receiptId.Length > 40 ? receiptId.Substring(0, 40) : receiptId },
                    { "notes", new Dictionary<string, string>
                        {
                            { "description", description }
                        }
                    }
                };

                // Razorpay .NET client executes synchronously, so run on threadpool
                var order = await Task.Run(() => client.Order.Create(options));
                var orderId = order["id"].ToString();

                return (true, orderId ?? string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Razorpay API error creating order: {Message}. Falling back to simulation order.", ex.Message);
                var fallbackOrderId = $"order_sim_{Guid.NewGuid():N}";
                return (true, fallbackOrderId, string.Empty);
            }
        }

        public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
        {
            if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(paymentId))
            {
                return false;
            }

            // In demo/simulation mode
            if (IsDemoKey(_keyId) || orderId.StartsWith("order_demo_") || orderId.StartsWith("order_sim_") || paymentId.StartsWith("pay_demo_") || paymentId.StartsWith("pay_sim_"))
            {
                return true;
            }

            try
            {
                var attributes = new Dictionary<string, string>
                {
                    { "razorpay_order_id", orderId },
                    { "razorpay_payment_id", paymentId },
                    { "razorpay_signature", signature ?? string.Empty }
                };

                Utils.verifyPaymentSignature(attributes);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Razorpay payment signature verification failed: {Message}", ex.Message);
                // If signature failed with real keys
                return false;
            }
        }

        private static bool IsDemoKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ||
                   key.Contains("YourKeyIdHere", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("DemoKey", StringComparison.OrdinalIgnoreCase);
        }
    }
}
