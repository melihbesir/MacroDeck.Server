using System;
using System.Timers;
using NAudio.CoreAudioApi;

namespace MacroDeck.Server.Services;

public class AudioManager : IDisposable
{
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private MMDevice? _defaultDevice;
    private readonly System.Timers.Timer _debounceTimer;
    private bool _isMuted = false;
    private bool _ignoreNextNotification = false;

    public event Action<int>? VolumeChangedImmediate;
    public event Action<int>? DebouncedVolumeChanged;

    public AudioManager()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        InitializeDevice();

        // 3 saniyelik gecikme / debounce zamanlayıcısı
        _debounceTimer = new System.Timers.Timer(3000)
        {
            AutoReset = false
        };
        _debounceTimer.Elapsed += OnDebounceTimerElapsed;
    }

    private void InitializeDevice()
    {
        try
        {
            _defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                _defaultDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotificationReceived;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audio init error: {ex.Message}");
        }
    }

    private void OnVolumeNotificationReceived(AudioVolumeNotificationData data)
    {
        if (_ignoreNextNotification)
        {
            _ignoreNextNotification = false;
            return;
        }

        int currentVol = (int)Math.Round(data.MasterVolume * 100);
        _isMuted = data.Muted;

        // PC tarafında ses değiştiğinde anında yerel UI güncellenebilir
        VolumeChangedImmediate?.Invoke(currentVol);

        // Ancak telefona bildirim göndermeden önce 3 saniye beklet (Debounce)
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        int currentVol = GetMasterVolume();
        DebouncedVolumeChanged?.Invoke(currentVol);
    }

    public int GetMasterVolume()
    {
        try
        {
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                return (int)Math.Round(_defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            }
        }
        catch { }
        return 50;
    }

    public void SetMasterVolume(int volumePercent, bool fromRemote = false)
    {
        try
        {
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                float level = Math.Clamp(volumePercent / 100f, 0f, 1f);

                if (fromRemote)
                {
                    // Telefon tarafından geldiyse, PC'deki bildirimi ve 3sn geri yansımayı sustur
                    _ignoreNextNotification = true;
                    _debounceTimer.Stop();
                }

                _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar = level;
                VolumeChangedImmediate?.Invoke(volumePercent);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Volume set error: {ex.Message}");
        }
    }

    public void ToggleMute()
    {
        try
        {
            if (_defaultDevice?.AudioEndpointVolume != null)
            {
                _defaultDevice.AudioEndpointVolume.Mute = !_defaultDevice.AudioEndpointVolume.Mute;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        if (_defaultDevice?.AudioEndpointVolume != null)
        {
            _defaultDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotificationReceived;
        }
        _defaultDevice?.Dispose();
        _deviceEnumerator?.Dispose();
    }
}
