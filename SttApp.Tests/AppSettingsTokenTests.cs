using Xunit;
using SttApp;

namespace SttApp.Tests;

public class AppSettingsTokenTests
{
    [Fact]
    public void GenerateAuthToken_HasReasonableLength()
    {
        var t = AppSettings.GenerateAuthTokenInternal();

        // 24 random bytes -> base64 (32 chars) - padding stripped → ~32
        Assert.InRange(t.Length, 28, 40);
    }

    [Fact]
    public void GenerateAuthToken_IsUrlSafe()
    {
        for (int i = 0; i < 100; i++)
        {
            var t = AppSettings.GenerateAuthTokenInternal();
            Assert.DoesNotContain('+', t);
            Assert.DoesNotContain('/', t);
            Assert.DoesNotContain('=', t);
            Assert.DoesNotContain(' ', t);
        }
    }

    [Fact]
    public void GenerateAuthToken_ProducesUniqueValues()
    {
        var set = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
            Assert.True(set.Add(AppSettings.GenerateAuthTokenInternal()),
                "Token should be unique each call");
    }

    [Fact]
    public void GenerateAuthToken_OnlyAllowedCharacters()
    {
        for (int i = 0; i < 50; i++)
        {
            var t = AppSettings.GenerateAuthTokenInternal();
            foreach (var c in t)
                Assert.True(char.IsLetterOrDigit(c) || c == '-' || c == '_',
                    $"Unexpected character '{c}' in token '{t}'");
        }
    }
}
