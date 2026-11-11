using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NodeX.Core.Audio;

namespace NodeX.UI.Desktop.Audio;

/// <summary>
/// Lists microphones/speakers for the Settings pickers and turns a saved device name back into the index the
/// backend wants (NAudio/MME numbers on Windows, PortAudio indexes elsewhere). Names are the saved ids because
/// indexes shift when devices come and go. Both lists use the same source as the backend that will open them.
/// </summary>
public static class DesktopAudioDevices
{
    public static AudioDeviceCatalog List()
    {
        try
        {
            var (inputs, outputs) = Enumerate();
            return new AudioDeviceCatalog(ToOptions(inputs), ToOptions(outputs));
        }
        catch (Exception)
        {
            // No driver / library: the pickers just offer "System default".
            return new AudioDeviceCatalog([], []);
        }
    }

    /// <summary>Index for a saved device name, or -1 (system default) when unset or no longer present.</summary>
    public static int Resolve(string? savedName, bool input)
    {
        if (string.IsNullOrEmpty(savedName)) return -1;
        try
        {
            var (inputs, outputs) = Enumerate();
            foreach (var (index, name) in input ? inputs : outputs)
                if (string.Equals(name, savedName, StringComparison.Ordinal)) return index;
        }
        catch (Exception) { /* fall through to default */ }
        return -1;
    }

    private static IReadOnlyList<AudioDeviceOption> ToOptions(List<(int Index, string Name)> devices) =>
        devices.Select(d => d.Name).Distinct(StringComparer.Ordinal).Select(n => new AudioDeviceOption(n, n)).ToList();

    private static (List<(int Index, string Name)> Inputs, List<(int Index, string Name)> Outputs) Enumerate()
    {
        var inputs = new List<(int, string)>();
        var outputs = new List<(int, string)>();

        if (OperatingSystem.IsWindows())
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                inputs.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
            for (int i = 0; i < WaveInterop.waveOutGetNumDevs(); i++)
                outputs.Add((i, OutputName(i)));
        }
        else if (PortAudioDeviceService.CheckAvailable() == null)
        {
            foreach (var d in PortAudioDeviceService.ListDevices())
            {
                if (d.InputChannels > 0) inputs.Add((d.Index, d.Name));
                if (d.OutputChannels > 0) outputs.Add((d.Index, d.Name));
            }
        }
        return (inputs, outputs);
    }

    // WaveOutEvent has no static device list, so ask the MME interop for the same names it opens by number.
    private static string OutputName(int deviceNumber)
    {
        var caps = new WaveOutCapabilities();
        var result = WaveInterop.waveOutGetDevCaps((IntPtr)deviceNumber, out caps, Marshal.SizeOf(caps));
        return (int)result == 0 ? caps.ProductName : $"Speaker {deviceNumber}";
    }
}
