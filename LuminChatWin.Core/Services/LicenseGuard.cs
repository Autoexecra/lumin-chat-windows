using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuminChatWin.Core.Models;

namespace LuminChatWin.Core.Services;

public static class LicenseGuard
{
    public static LicenseValidationResult Validate(AppConfig config)
    {
        if (!config.License.Enabled)
        {
            return LicenseValidationResult.Success();
        }

        var path = ConfigService.ExpandPath(config.License.LicenseFile);
        if (!File.Exists(path))
        {
            return LicenseValidationResult.Fail($"许可证文件不存在: {path}");
        }

        try
        {
            var payload = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var subject = payload.TryGetProperty("subject", out var subjectElement) ? subjectElement.GetString() ?? string.Empty : string.Empty;
            var expiresAt = payload.TryGetProperty("expires_at", out var expiresElement) ? expiresElement.GetString() ?? string.Empty : string.Empty;
            var signature = payload.TryGetProperty("signature", out var signatureElement) ? signatureElement.GetString() ?? string.Empty : string.Empty;

            if (!string.Equals(subject, config.License.Subject, StringComparison.OrdinalIgnoreCase))
            {
                return LicenseValidationResult.Fail("许可证 subject 不匹配。");
            }

            if (!DateTimeOffset.TryParse(expiresAt, out var expires) || expires < DateTimeOffset.UtcNow)
            {
                return LicenseValidationResult.Fail("许可证已过期或格式无效。");
            }

            var secret = string.IsNullOrWhiteSpace(config.License.Secret)
                ? Environment.GetEnvironmentVariable(config.License.SecretEnv)
                : config.License.Secret;

            if (!string.IsNullOrWhiteSpace(secret))
            {
                var signatureBase = $"{subject}\n{expiresAt}";
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));
                if (!string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase))
                {
                    return LicenseValidationResult.Fail("许可证签名校验失败。");
                }
            }

            return LicenseValidationResult.Success();
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail($"许可证解析失败: {ex.Message}");
        }
    }
}