using System;

namespace Octave_Desktop.Services.System;

public interface ISmtcService : IDisposable
{
    void Initialize(IntPtr windowHandle);
}
