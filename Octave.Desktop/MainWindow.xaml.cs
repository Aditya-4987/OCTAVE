using Microsoft.UI.Xaml;
using Octave.Core.Services.Audio;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Octave_Desktop;

public sealed partial class MainWindow : Window
{
    private readonly IAudioPlayerService _audioPlayer = new ManagedBassAudioService();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr hWnd,
        string text,
        string caption,
        uint type);

    public MainWindow()
    {
        try
        {
            InitializeComponent();

            string testSongPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                @"Assets\test.mp3");

            bool initialized = _audioPlayer.Init();

            _audioPlayer.Play(testSongPath);
        }
        catch (Exception ex)
        {
            MessageBox(
                IntPtr.Zero,
                ex.ToString(),
                "Octave Crash",
                0);
        }
    }
}