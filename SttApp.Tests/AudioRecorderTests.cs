using Xunit;
using SttApp;

namespace SttApp.Tests;

public class AudioRecorderTests
{
    [Fact]
    public void GetInputDeviceCount_DoesNotThrow()
    {
        // Voláme jen API – v CI bez mikrofonu by to mělo vrátit 0, ne hodit výjimku
        var count = AudioRecorder.GetInputDeviceCount();
        Assert.True(count >= 0);
    }

    [Fact]
    public void GetDefaultInputDeviceName_NeverThrows()
    {
        // Pokud žádný mic není, vrací null; jinak nějaký string
        var name = AudioRecorder.GetDefaultInputDeviceName();
        if (AudioRecorder.GetInputDeviceCount() == 0)
            Assert.Null(name);
        else
            Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void AudioDeviceException_PreservesMessageAndInner()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new AudioRecorder.AudioDeviceException("outer", inner);

        Assert.Equal("outer", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
