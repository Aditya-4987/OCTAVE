namespace Octave.Core.Interfaces;

public interface ILibraryWatcherService
{
    void AddMonitoredPath(string path);
    void RemoveMonitoredPath(string path);
}
